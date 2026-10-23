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
/// Task 2 scope: authenticated requests are parsed as a structured JSON
/// document, re-validated against the agent's own allowlist (D18), and
/// dispatched to <see cref="ISystemctlRunner"/> with fixed argv (D19).
/// </summary>
public sealed class AgentSocketListener : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly byte[] _secret;
    private readonly UnitAllowlist _allowlist;
    private readonly ISystemctlRunner _runner;
    private readonly ILogger<AgentSocketListener> _logger;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private Socket? _listener;

    public AgentSocketListener(
        AgentOptions options,
        byte[] secret,
        UnitAllowlist allowlist,
        ISystemctlRunner runner,
        ILogger<AgentSocketListener> logger)
    {
        _options = options;
        _secret = secret;
        _allowlist = allowlist;
        _runner = runner;
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
        // Explicit socket file mode (review F3): do not depend on the process
        // umask. 660 = owner + shared group only; the containing directory
        // (0750, root-owned) remains the primary access control.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _options.SocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
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

    /// <summary>Visible for testing: read, authenticate, dispatch, respond.</summary>
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

        var request = ParseRequest(remainder);
        if (request is null)
        {
            await WriteResponseAsync(client, new AgentResponse(
                RequestId: null, Outcome: "Rejected",
                Detail: "Malformed request."), cancellationToken);
            return AgentRequestOutcome.Rejected;
        }

        // Agent-side independent re-validation (D18): the agent never trusts
        // API-side validation. Action must be in the fixed set (D23) and the
        // unit must exactly match the agent's own allowlist.
        if (!AgentActions.IsDefined(request.Action))
        {
            await WriteResponseAsync(client, new AgentResponse(
                RequestId: request.RequestId, Outcome: "Rejected",
                Detail: "Unknown action."), cancellationToken);
            return AgentRequestOutcome.Rejected;
        }

        // Health probe (task 3): authenticated liveness check, no process, no
        // systemd interaction. Handled before allowlist/unit validation since
        // a ping carries no unit.
        if (request.Action == AgentActions.Ping)
        {
            await WriteResponseAsync(client, new AgentResponse(
                RequestId: request.RequestId, Outcome: "Success",
                Detail: "pong"), cancellationToken);
            return AgentRequestOutcome.Accepted;
        }

        if (!_allowlist.Contains(request.Unit!))
        {
            // Generic rejection: never reveals which units are allowlisted.
            await WriteResponseAsync(client, new AgentResponse(
                RequestId: request.RequestId, Outcome: "Rejected",
                Detail: "Unit is not permitted."), cancellationToken);
            return AgentRequestOutcome.Rejected;
        }

        var result = await _runner.RunAsync(request.Action!, request.Unit!, cancellationToken);
        var outcome = result.Succeeded ? "Success" : "Failure";
        var detail = result.TimedOut
            ? "Command timed out and was killed."
            : (result.Output.Length > 0 ? result.Output : $"systemctl exited with {result.ExitCode}.");

        await WriteResponseAsync(client, new AgentResponse(
            RequestId: request.RequestId, Outcome: outcome, Detail: detail),
            cancellationToken);
        return result.Succeeded ? AgentRequestOutcome.Accepted : AgentRequestOutcome.Rejected;
    }

    /// <summary>
    /// Parses the JSON request document. Returns null on any parse failure —
    /// malformed input is rejected, never partially honored (TA5).
    /// </summary>
    private static AgentRequest? ParseRequest(string json)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize(json, AgentRequestJson.Default.AgentRequest);
            return request is not null &&
                   !string.IsNullOrWhiteSpace(request.Action) &&
                   // Mutations require a unit; the ping health probe does not.
                   (request.Action == AgentActions.Ping ||
                    !string.IsNullOrWhiteSpace(request.Unit))
                ? request
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentResponse))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
