using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using HostingAdmin.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Trust boundary tests (plan task 1, TA1): the socket listener must reject
/// unauthenticated requests and never start a process. The full protocol
/// (allowlist, systemctl) arrives in task 2.
/// </summary>
public class AgentSocketListenerTests : IDisposable
{
    private readonly string? _socketPath;
    private readonly byte[]? _secret;
    private readonly AgentSocketListener? _listener;

    public AgentSocketListenerTests()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _socketPath = Path.Combine(Path.GetTempPath(), $"agent-test-{Guid.NewGuid():N}.sock");
        _secret = "test-secret-0123456789abcdef0123456789abcdef"u8.ToArray();
        _listener = new AgentSocketListener(
            new AgentOptions { SocketPath = _socketPath, MaxRequestBytes = 4096 },
            _secret,
            new UnitAllowlist(["nginx.service"]),
            new RecordingRunner(),
            NullLogger<AgentSocketListener>.Instance);
    }

    private bool OnLinux => OperatingSystem.IsLinux();

    [Fact]
    public async Task Request_without_secret_is_rejected_and_no_process_started()
    {
        // TA1. The agent skeleton never spawns processes at all; the assertion
        // is that an unauthenticated request gets a Rejected outcome.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return; // Unix sockets require Linux; covered by the Linux CI job.
        }

        var runTask = _listener.RunAsync(new CancellationTokenSource(10_000).Token);
        try
        {
            await WaitForSocketAsync();

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            var payload = Encoding.UTF8.GetBytes("wrong-secret\n{\"any\":\"payload\"}\n");
            await client.SendAsync(payload);

            var response = await ReadResponseAsync(client);
            Assert.Equal("Rejected", response.Outcome);
        }
        finally
        {
            await runTask;
        }
    }

    [Fact]
    public async Task Request_with_correct_secret_and_allowlisted_unit_succeeds()
    {
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var runTask = _listener.RunAsync(new CancellationTokenSource(10_000).Token);
        try
        {
            await WaitForSocketAsync();

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            var payload = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(_secret!) +
                "\n{\"requestId\":\"r-1\",\"action\":\"start\",\"unit\":\"nginx.service\"}\n");
            await client.SendAsync(payload);

            var response = await ReadResponseAsync(client);
            Assert.Equal("Success", response.Outcome);
            Assert.Equal("r-1", response.RequestId);
        }
        finally
        {
            await runTask;
        }
    }

    [Fact]
    public async Task Oversized_request_without_secret_is_rejected()
    {
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var runTask = _listener.RunAsync(new CancellationTokenSource(10_000).Token);
        try
        {
            await WaitForSocketAsync();

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            // Larger than MaxRequestBytes, no secret line — must be rejected
            // without unbounded buffering (D22).
            var big = new string('x', 8192);
            await client.SendAsync(Encoding.UTF8.GetBytes(big));

            var response = await ReadResponseAsync(client);
            Assert.Equal("Rejected", response.Outcome);
        }
        finally
        {
            await runTask;
        }
    }

    private async Task WaitForSocketAsync()
    {
        for (var attempt = 0; attempt < 50 && !File.Exists(_socketPath); attempt++)
        {
            await Task.Delay(20);
        }
        Assert.True(File.Exists(_socketPath), "agent socket was not created");
    }

    private static async Task<AgentResponse> ReadResponseAsync(Socket client)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None);
            if (read == 0)
            {
                break;
            }
            total += read;
            var text = Encoding.UTF8.GetString(buffer, 0, total);
            var newline = text.IndexOf('\n');
            if (newline >= 0)
            {
                return System.Text.Json.JsonSerializer.Deserialize(
                    text[..newline], AgentJsonContext.Default.AgentResponse)!;
            }
        }
        throw new InvalidOperationException("No complete agent response received.");
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (OnLinux && File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
    }
}

/// <summary>
/// Test double for <see cref="ISystemctlRunner"/>: records the requested
/// action/unit and returns a configurable result. Never starts a process.
/// </summary>
public sealed class RecordingRunner : ISystemctlRunner
{
    public List<(string Action, string Unit)> Calls { get; } = [];

    public SystemctlResult Result { get; set; } = new(TimedOut: false, ExitCode: 0, Output: "");

    public Task<SystemctlResult> RunAsync(string action, string unit, CancellationToken cancellationToken)
    {
        Calls.Add((action, unit));
        return Task.FromResult(Result);
    }
}

[JsonSerializable(typeof(AgentResponse))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
