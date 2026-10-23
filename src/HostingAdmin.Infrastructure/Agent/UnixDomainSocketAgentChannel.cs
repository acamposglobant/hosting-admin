using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostingAdmin.Core.Agent;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Agent;

/// <summary>
/// Real API→agent channel (plan task 3): one request/response exchange per
/// unix-socket connection, shared secret as the first line (D22), JSON
/// document after it (plan §3.3 wire format, camelCase). Per-call timeout
/// (D22); every failure maps to a structured result — the channel never
/// throws to callers. On non-Linux hosts this class is not registered; the
/// NullAgentChannel reports structured "unavailable" instead (D17/D24).
/// </summary>
public sealed class UnixDomainSocketAgentChannel : IAgentChannel
{
    private readonly AgentChannelOptions _options;
    private readonly byte[] _secret;
    private readonly ILogger<UnixDomainSocketAgentChannel> _logger;

    public UnixDomainSocketAgentChannel(
        AgentChannelOptions options, byte[] secret, ILogger<UnixDomainSocketAgentChannel> logger)
    {
        _options = options;
        _secret = secret;
        _logger = logger;
    }

    public bool IsAvailable => OperatingSystem.IsLinux();

    public async Task<AgentChannelResult> SendServiceActionAsync(
        string unit, string action, CancellationToken cancellationToken)
    {
        return await ExchangeAsync(
            new AgentWireRequest(null, action, unit), _options.CallTimeout, cancellationToken);
    }

    /// <summary>
    /// Health probe (task 3): authenticated ping with a short timeout. Used to
    /// compute <c>mutationCapabilityAvailable</c> on GET /api/services without
    /// slowing polling when the agent is down.
    /// </summary>
    public async Task<AgentChannelResult> ProbeAsync(CancellationToken cancellationToken)
    {
        // "ping" mirrors HostingAdmin.Agent.AgentActions.Ping (the agent project
        // is intentionally not referenced from Infrastructure — the channel
        // speaks the wire protocol, not the agent's types).
        return await ExchangeAsync(
            new AgentWireRequest(null, "ping", null),
            _options.ProbeTimeout, cancellationToken);
    }

    /// <summary>
    /// Single connection, single request, single response. All failures —
    /// connect, timeout, protocol — collapse into structured results (D24);
    /// exception details stay in the log, never in API responses.
    /// </summary>
    private async Task<AgentChannelResult> ExchangeAsync(
        AgentWireRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return AgentChannelResult.Unavailable;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.SocketPath), linked.Token);

            var payload = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(_secret) + "\n" + JsonSerializer.Serialize(request, WireJsonContext.Default.AgentWireRequest) + "\n");
            await socket.SendAsync(payload, linked.Token);

            var response = await ReadResponseAsync(socket, linked.Token);
            if (response is null)
            {
                _logger.LogWarning("Agent response was incomplete or oversized");
                return AgentChannelResult.Unavailable;
            }

            var outcome = response.Outcome switch
            {
                "Success" => AgentOperationOutcome.Success,
                "Failure" => AgentOperationOutcome.Failure,
                _ => AgentOperationOutcome.Rejected,
            };
            return new AgentChannelResult(outcome, response.Detail, response.RequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Call timeout (D22): structured unavailable, not an error surface.
            _logger.LogWarning("Agent call timed out after {Timeout}", timeout);
            return AgentChannelResult.Unavailable;
        }
        catch (SocketException ex)
        {
            // Agent down / socket missing (D24): structured unavailable. The
            // exception (which contains the socket path) is logged, not returned.
            _logger.LogWarning("Agent channel connect failed: {ErrorCode}", ex.SocketErrorCode);
            return AgentChannelResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent channel exchange failed: {ExceptionType}", ex.GetType().Name);
            return AgentChannelResult.Unavailable;
        }
    }

    /// <summary>
    /// Reads one bounded response line. The response is tiny JSON; anything
    /// larger than the bound is treated as a protocol violation.
    /// </summary>
    private async Task<AgentWireResponse?> ReadResponseAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return null;
            }
            total += read;

            var received = Encoding.UTF8.GetString(buffer, 0, total);
            var newline = received.IndexOf('\n');
            if (newline >= 0)
            {
                return JsonSerializer.Deserialize(received[..newline], WireJsonContext.Default.AgentWireResponse);
            }
        }

        return null;
    }
}

/// <summary>Request document on the API→agent wire (camelCase, plan §3.3).</summary>
public sealed record AgentWireRequest(string? RequestId, string Action, string? Unit);

/// <summary>Response document from the agent (camelCase, plan §3.3).</summary>
public sealed record AgentWireResponse(string? RequestId, string Outcome, string Detail);

/// <summary>JSON source generation for the channel wire format.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentWireRequest))]
[JsonSerializable(typeof(AgentWireResponse))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
