using System.Net.Sockets;
using System.Text;
using HostingAdmin.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Protocol tests over the real unix socket with a mocked runner (TA2, TA5):
/// non-allowlisted units and malformed requests are rejected and the runner is
/// never invoked. Skipped on Windows (Linux CI covers them).
/// </summary>
public class AgentProtocolTests : IDisposable
{
    private readonly string? _socketPath;
    private readonly byte[]? _secret;
    private readonly AgentSocketListener? _listener;
    private readonly RecordingRunner? _runner;

    private const string Secret = "test-secret-0123456789abcdef0123456789abcdef";

    public AgentProtocolTests()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _socketPath = Path.Combine(Path.GetTempPath(), $"agent-proto-{Guid.NewGuid():N}.sock");
        _secret = Encoding.UTF8.GetBytes(Secret);
        _runner = new RecordingRunner();
        _listener = new AgentSocketListener(
            new AgentOptions { SocketPath = _socketPath, MaxRequestBytes = 4096 },
            _secret,
            new UnitAllowlist(["nginx.service"]),
            _runner,
            NullLogger<AgentSocketListener>.Instance);
    }

    private bool OnLinux => OperatingSystem.IsLinux();

    [Fact]
    public async Task Non_allowlisted_unit_is_rejected_and_runner_never_called()
    {
        // TA2: the agent re-validates independently (D18); a valid secret with
        // a non-allowlisted unit must not reach the runner.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync(
            "{\"requestId\":\"r-2\",\"action\":\"start\",\"unit\":\"sshd.service\"}");
        Assert.Equal("Rejected", response.Outcome);
        Assert.Equal("r-2", response.RequestId);
        Assert.Empty(_runner!.Calls);
    }

    [Fact]
    public async Task Ping_action_succeeds_without_touching_runner()
    {
        // Task 3 health probe: authenticated ping → Success, no process spawn.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync(
            "{\"requestId\":\"r-ping\",\"action\":\"ping\",\"unit\":null}");
        Assert.Equal("Success", response.Outcome);
        Assert.Empty(_runner!.Calls);
    }

    [Fact]
    public async Task Unknown_action_is_rejected_and_runner_never_called()
    {
        // D23: only start/stop/restart exist — no enable/disable/mask.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync(
            "{\"requestId\":\"r-3\",\"action\":\"enable\",\"unit\":\"nginx.service\"}");
        Assert.Equal("Rejected", response.Outcome);
        Assert.Empty(_runner!.Calls);
    }

    [Theory]
    [InlineData("{\"requestId\":\"r-4\",\"action\":\"start\",\"unit\":\"nginx.service; reboot\"}")]
    [InlineData("{\"requestId\":\"r-5\",\"action\":\"start\",\"unit\":\"../../evil\"}")]
    [InlineData("{\"requestId\":\"r-6\",\"action\":\"start\",\"unit\":\"nginx service\"}")]
    [InlineData("{\"requestId\":\"r-7\",\"action\":\"start\",\"unit\":\"nginx\\u200B.service\"}")]
    public async Task Injection_and_traversal_payloads_are_rejected_before_runner(string requestJson)
    {
        // TA3 at the protocol level: charset + allowlist reject payloads; the
        // runner (and therefore any process) is never invoked.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync(requestJson);
        Assert.Equal("Rejected", response.Outcome);
        Assert.Empty(_runner!.Calls);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_and_runner_never_called()
    {
        // TA5: malformed request → clean rejection, no process.
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync("{not valid json");
        Assert.Equal("Rejected", response.Outcome);
        Assert.Empty(_runner!.Calls);
    }

    [Fact]
    public async Task Missing_unit_or_action_is_rejected()
    {
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var response = await ExchangeAsync("{\"requestId\":\"r-8\",\"action\":\"start\"}");
        Assert.Equal("Rejected", response.Outcome);
        Assert.Empty(_runner!.Calls);
    }

    [Fact]
    public async Task Failed_systemctl_exit_maps_to_failure_outcome()
    {
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        _runner!.Result = new SystemctlResult(TimedOut: false, ExitCode: 1, Output: "Job failed.");
        var response = await ExchangeAsync(
            "{\"requestId\":\"r-9\",\"action\":\"stop\",\"unit\":\"nginx.service\"}");
        Assert.Equal("Failure", response.Outcome);
        Assert.Contains("Job failed.", response.Detail);
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Timed_out_command_maps_to_failure_with_timeout_detail()
    {
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        _runner!.Result = new SystemctlResult(TimedOut: true, ExitCode: null, Output: "Command timed out and was killed.");
        var response = await ExchangeAsync(
            "{\"requestId\":\"r-10\",\"action\":\"restart\",\"unit\":\"nginx.service\"}");
        Assert.Equal("Failure", response.Outcome);
        Assert.Contains("timed out", response.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrency_cap_rejects_excess_connections_cleanly()
    {
        // TA6: with MaxConcurrentConnections=1 and one in-flight request, a
        // second connection is closed without a response (no queue, no hang).
        if (!OnLinux || _listener is null || _socketPath is null)
        {
            return;
        }

        var blockingRunner = new BlockingRunner();
        var socketPath = _socketPath!;
        var listener = new AgentSocketListener(
            new AgentOptions { SocketPath = socketPath, MaxConcurrentConnections = 1 },
            _secret!,
            new UnitAllowlist(["nginx.service"]),
            blockingRunner,
            NullLogger<AgentSocketListener>.Instance);

        var runTask = listener.RunAsync(new CancellationTokenSource(15_000).Token);
        try
        {
            await WaitForSocketAsync(socketPath);

            // First connection: in-flight (runner blocked).
            var first = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            first.Connect(new UnixDomainSocketEndPoint(socketPath));
            await first.SendAsync(Encoding.UTF8.GetBytes(
                Secret + "\n{\"requestId\":\"b-1\",\"action\":\"start\",\"unit\":\"nginx.service\"}\n"));

            // Give the server time to accept and occupy its single slot.
            await Task.Delay(300);

            // Second connection: must be disposed by the cap without a response.
            using var second = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            second.Connect(new UnixDomainSocketEndPoint(socketPath));
            await second.SendAsync(Encoding.UTF8.GetBytes(
                Secret + "\n{\"requestId\":\"b-2\",\"action\":\"start\",\"unit\":\"nginx.service\"}\n"));
            var closed = await Task.Run(() => second.Poll(1_000_000, SelectMode.SelectRead) && second.Available == 0);
            Assert.True(closed, "excess connection was not closed by the concurrency cap");

            // Unblock the first request; it must complete normally.
            blockingRunner.Release();
            var response = await ReadResponseAsync(first);
            Assert.Equal("Success", response.Outcome);
            first.Dispose();
        }
        finally
        {
            await runTask;
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task<AgentResponse> ExchangeAsync(string requestJson)
    {
        var socketPath = _socketPath!;
        var runTask = _listener!.RunAsync(new CancellationTokenSource(10_000).Token);
        try
        {
            await WaitForSocketAsync(socketPath);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            await client.SendAsync(Encoding.UTF8.GetBytes(Secret + "\n" + requestJson + "\n"));
            return await ReadResponseAsync(client);
        }
        finally
        {
            await runTask;
        }
    }

    private static async Task WaitForSocketAsync(string socketPath)
    {
        for (var attempt = 0; attempt < 50 && !File.Exists(socketPath); attempt++)
        {
            await Task.Delay(20);
        }
        Assert.True(File.Exists(socketPath), "agent socket was not created");
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

/// <summary>Runner that blocks until released — used for the concurrency test.</summary>
public sealed class BlockingRunner : ISystemctlRunner
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public async Task<SystemctlResult> RunAsync(string action, string unit, CancellationToken cancellationToken)
    {
        await _release.Task.WaitAsync(cancellationToken);
        return new SystemctlResult(TimedOut: false, ExitCode: 0, Output: "");
    }
}
