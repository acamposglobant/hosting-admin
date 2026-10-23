using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostingAdmin.Agent;

/// <summary>
/// Outcome of a single socket request handled by the agent.
/// </summary>
public enum AgentRequestOutcome
{
    /// <summary>Request authenticated and accepted (task 2 will dispatch it).</summary>
    Accepted,

    /// <summary>Request rejected — bad secret, malformed, or oversized.</summary>
    Rejected,

    /// <summary>The connection failed at the transport level.</summary>
    ConnectionError,
}

/// <summary>
/// Unix domain socket listener for the privileged agent (D17, D22).
///
/// Trust boundary (plan §3.2):
/// - The agent listens ONLY on a unix domain socket — never TCP, never a
///   public interface. File permissions on the socket are the primary control.
/// - Every request must carry the shared secret (defense in depth); the
///   comparison is constant-time to avoid timing oracles.
/// - One request per connection, bounded size, bounded concurrency, hard
///   per-request timeout.
///
/// Task 1 scope: skeleton + authentication. Authenticated requests receive a
/// minimal acknowledgement; the allowlist and systemctl execution arrive in
/// task 2 — this class never spawns processes.
/// </summary>
public sealed class AgentSocketListener : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly byte[] _secret;
    private readonly ILogger<AgentSocketListener> _logger;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private Socket? _listener;

    public AgentSocketListener(AgentOptions options, byte[] secret, ILogger<AgentSocketListener> logger)
    {
        _options = options;
        _secret = secret;
        _logger = logger;
        _connectionSlots = new SemaphoreSlim(options.MaxConcurrentConnections);
    }

    /// <summary>
    /// Binds the unix socket and serves requests until the shutdown token
    /// fires. The socket file is removed on shutdown so a stale socket never
    /// outlives the process.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

        // Remove a stale socket file from a previous unclean shutdown; bind
        // would otherwise fail with "address already in use".
        if (File.Exists(_options.SocketPath))
        {
            File.Delete(_options.SocketPath);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_options.SocketPath));
        // Backlog is small: the agent serves at most MaxConcurrentConnections
        // requests at a time and rejects excess cleanly (D22).
        _listener.Listen(_options.MaxConcurrentConnections);
        _logger.LogInformation("Agent listening on unix socket {SocketPath}", _options.SocketPath);

        var handlers = new List<Task>();
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(linked.Token);
                if (!_connectionSlots.Wait(0))
                {
                    // Concurrency cap (D22): reject cleanly, do not queue.
                    _logger.LogWarning("Concurrency cap reached; rejecting connection");
                    client.Dispose();
                    continue;
                }

                handlers.Add(HandleConnectionAsync(client, linked.Token));
                if (handlers.Count > 16)
                {
                    handlers.RemoveAll(h => h.IsCompleted);
                }
            }
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _listener.Dispose();
            await Task.WhenAll(handlers);
            TryDeleteSocketFile();
        }
    }

    /// <summary>
    /// Handles one connection: read one bounded request, authenticate it,
    /// respond with a single JSON document, close. Never throws.
    /// </summary>
    internal async Task HandleConnectionAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
                var outcome = await ProcessRequestAsync(client, timeout.Token);
                _logger.LogInformation("Agent request handled: {Outcome}", outcome);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Agent request timed out or was cancelled");
        }
        catch (Exception ex)
        {
            // Structured log without request contents (D25: no secrets, no
            // untrusted payloads in logs).
            _logger.LogError(ex, "Agent connection failed");
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    /// <summary>Visible for testing: read, authenticate, respond.</summary>
    internal async Task<AgentRequestOutcome> ProcessRequestAsync(Socket client, CancellationToken cancellationToken)
    {
        var (secretProvided, remainder) = await ReadUntilSecretAsync(client, cancellationToken);
        if (!secretProvided)
        {
            await WriteResponseAsync(client, new AgentResponse(
                RequestId: null, Outcome: "Rejected",
                Detail: "Authentication failed."), cancellationToken);
            return AgentRequestOutcome.Rejected;
        }

        // Task 1 skeleton: authenticated requests are acknowledged. Task 2
        // replaces this with the structured request protocol (allowlist +
        // systemctl runner). No process is ever started here.
        await WriteResponseAsync(client, new AgentResponse(
            RequestId: null, Outcome: "Accepted",
            Detail: "Authenticated. Request processing is not implemented yet (task 2)."),
            cancellationToken);
        return AgentRequestOutcome.Accepted;
    }

    /// <summary>
    /// Reads the request until the shared secret line is seen. The secret must
    /// be the first line of the request. Comparison is constant-time (D22).
    /// Returns false when the secret is missing or wrong; the connection is
    /// then answered with a rejection and closed.
    /// </summary>
    private async Task<(bool ok, string remainder)> ReadUntilSecretAsync(
        Socket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return (false, string.Empty);
            }
            total += read;

            var received = Encoding.UTF8.GetString(buffer, 0, total);
            var newline = received.IndexOf('\n');
            if (newline < 0)
            {
                continue;
            }

            var providedSecret = received[..newline];
            var remainder = received[(newline + 1)..];
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(providedSecret), _secret))
            {
                return (false, string.Empty);
            }
            return (true, remainder);
        }

        // Oversized request: no secret line within the size bound (D22).
        return (false, string.Empty);
    }

    private static async Task WriteResponseAsync(
        Socket client, AgentResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, AgentJsonContext.Default.AgentResponse);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await client.SendAsync(payload, cancellationToken);
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(_options.SocketPath))
            {
                File.Delete(_options.SocketPath);
            }
        }
        catch (IOException)
        {
            // Best effort; the startup path also removes stale sockets.
        }
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _connectionSlots.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Response document written to the socket (plan §3.3 shape).</summary>
public sealed record AgentResponse(string? RequestId, string Outcome, string Detail);

/// <summary>JSON source generation context for the agent protocol.</summary>
[JsonSerializable(typeof(AgentResponse))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
