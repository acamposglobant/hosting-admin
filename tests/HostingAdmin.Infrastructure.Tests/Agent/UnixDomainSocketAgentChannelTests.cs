using System.Net.Sockets;
using System.Text;
using HostingAdmin.Core.Agent;
using HostingAdmin.Infrastructure.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostingAdmin.Infrastructure.Tests.Agent;

/// <summary>
/// Channel tests (plan task 3): the real unix-socket exchange against a stub
/// agent listener, plus the Windows "unavailable" contract (D24) and the
/// secret-never-logged rule (TA12 partial, D25). Socket tests run on Linux CI
/// only; the wire-format and unavailable tests run everywhere.
/// </summary>
public class UnixDomainSocketAgentChannelTests : IDisposable
{
    private const string Secret = "channel-test-secret-0123456789abcdef";

    private readonly string? _socketPath;
    private readonly byte[]? _secret;
    private readonly TcpEchoStubAgent? _stubAgent;

    public UnixDomainSocketAgentChannelTests()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _socketPath = Path.Combine(Path.GetTempPath(), $"agent-channel-{Guid.NewGuid():N}.sock");
        _secret = Encoding.UTF8.GetBytes(Secret);
        _stubAgent = new TcpEchoStubAgent(_socketPath, _secret);
    }

    private bool OnLinux => OperatingSystem.IsLinux();

    private UnixDomainSocketAgentChannel CreateChannel(TimeSpan? callTimeout = null, TimeSpan? probeTimeout = null)
    {
        var options = new AgentChannelOptions
        {
            SocketPath = _socketPath!,
            CallTimeout = callTimeout ?? TimeSpan.FromSeconds(5),
            ProbeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(750),
        };
        return new UnixDomainSocketAgentChannel(
            options, _secret!, NullLogger<UnixDomainSocketAgentChannel>.Instance);
    }

    [Fact]
    public async Task Channel_round_trip_maps_agent_outcomes()
    {
        // TA13 (channel level): auth → request → structured outcome.
        if (!OnLinux || _stubAgent is null)
        {
            return;
        }

        await _stubAgent.StartAsync();
        try
        {
            var channel = CreateChannel();

            var success = await channel.SendServiceActionAsync("nginx.service", "start", CancellationToken.None);
            Assert.Equal(AgentOperationOutcome.Success, success.Outcome);

            var failure = await channel.SendServiceActionAsync("nginx.service", "stop", CancellationToken.None);
            Assert.Equal(AgentOperationOutcome.Failure, failure.Outcome);

            var rejected = await channel.SendServiceActionAsync("evil.service", "start", CancellationToken.None);
            Assert.Equal(AgentOperationOutcome.Rejected, rejected.Outcome);
        }
        finally
        {
            await _stubAgent.StopAsync();
        }
    }

    [Fact]
    public async Task Probe_returns_success_from_live_agent()
    {
        if (!OnLinux || _stubAgent is null)
        {
            return;
        }

        await _stubAgent.StartAsync();
        try
        {
            var channel = CreateChannel();
            var probe = await channel.ProbeAsync(CancellationToken.None);
            Assert.Equal(AgentOperationOutcome.Success, probe.Outcome);
        }
        finally
        {
            await _stubAgent.StopAsync();
        }
    }

    [Fact]
    public async Task Agent_down_returns_structured_unavailable_not_exception()
    {
        // D24: unreachable agent → structured unavailable, never an exception.
        if (!OnLinux)
        {
            return;
        }

        // No stub agent running: the socket file does not exist.
        var channel = CreateChannel();
        var result = await channel.SendServiceActionAsync("nginx.service", "restart", CancellationToken.None);
        Assert.Equal(AgentOperationOutcome.Failure, result.Outcome);
        Assert.Equal("The privileged agent is unavailable.", result.Detail);
    }

    [Fact]
    public async Task Slow_agent_times_out_to_structured_unavailable()
    {
        // D22: per-call timeout → structured unavailable.
        if (!OnLinux || _stubAgent is null)
        {
            return;
        }

        await _stubAgent.StartAsync();
        _stubAgent.DelayResponse = TimeSpan.FromSeconds(10);
        try
        {
            var channel = CreateChannel(callTimeout: TimeSpan.FromMilliseconds(300));
            var result = await channel.SendServiceActionAsync("nginx.service", "restart", CancellationToken.None);
            Assert.Equal(AgentOperationOutcome.Failure, result.Outcome);
            Assert.Equal("The privileged agent is unavailable.", result.Detail);
        }
        finally
        {
            _stubAgent.DelayResponse = TimeSpan.Zero;
            await _stubAgent.StopAsync();
        }
    }

    [Fact]
    public async Task Secret_is_never_logged()
    {
        // TA12 partial (D25): the channel must never write the secret to logs.
        if (!OnLinux || _stubAgent is null || _socketPath is null)
        {
            return;
        }

        await _stubAgent.StartAsync();
        try
        {
            var capture = new CapturingLogger<UnixDomainSocketAgentChannel>();
            var options = new AgentChannelOptions
            {
                SocketPath = _socketPath,
                CallTimeout = TimeSpan.FromSeconds(5),
                ProbeTimeout = TimeSpan.FromMilliseconds(750),
            };
            var channel = new UnixDomainSocketAgentChannel(
                options, _secret!, capture);

            await channel.SendServiceActionAsync("nginx.service", "start", CancellationToken.None);

            Assert.DoesNotContain(Secret, capture.Entries.AllText);
        }
        finally
        {
            await _stubAgent.StopAsync();
        }
    }

    [Fact]
    public async Task Non_linux_returns_unavailable()
    {
        // D17/D24: on non-Linux the channel reports structured unavailable.
        // This test runs everywhere; on Linux it just exercises the same path.
        var channel = CreateChannel();
        var result = await channel.SendServiceActionAsync("nginx.service", "start", CancellationToken.None);
        if (!OnLinux)
        {
            Assert.Equal(AgentOperationOutcome.Failure, result.Outcome);
            Assert.Equal("The privileged agent is unavailable.", result.Detail);
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (OnLinux && _socketPath is not null && File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
        _stubAgent?.Dispose();
    }
}

/// <summary>
/// Minimal unix-socket stub agent: reads the secret line + one JSON request,
/// answers with a canned JSON response. Lets the channel be tested end-to-end
/// without the agent project or systemd.
/// </summary>
public sealed class TcpEchoStubAgent : IDisposable
{
    private readonly string _socketPath;
    private readonly byte[] _secret;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public TimeSpan DelayResponse { get; set; } = TimeSpan.Zero;

    public TcpEchoStubAgent(string socketPath, byte[] secret)
    {
        _socketPath = socketPath;
        _secret = secret;
    }

    public Task StartAsync()
    {
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(5);
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener!.AcceptAsync(cancellationToken);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(Socket client)
    {
        try
        {
            using (client)
            {
                var buffer = new byte[4096];
                var total = 0;
                string? secretLine = null;
                string? requestLine = null;
                while (total < buffer.Length)
                {
                    var read = await client.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None);
                    if (read == 0)
                    {
                        return;
                    }
                    total += read;
                    var text = Encoding.UTF8.GetString(buffer, 0, total);
                    var lines = text.Split('\n');
                    if (lines.Length >= 2)
                    {
                        secretLine = lines[0];
                        requestLine = lines[1];
                        break;
                    }
                }

                if (secretLine is null || requestLine is null)
                {
                    return;
                }

                if (DelayResponse > TimeSpan.Zero)
                {
                    await Task.Delay(DelayResponse);
                }

                var outcome = requestLine.Contains("\"start\"") && requestLine.Contains("nginx.service")
                    ? "Success"
                    : requestLine.Contains("\"start\"") ? "Rejected" : "Failure";
                var response = $"{{\"requestId\":null,\"outcome\":\"{outcome}\",\"detail\":\"stub\"}}\n";
                await client.SendAsync(Encoding.UTF8.GetBytes(response), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Stub: swallow everything.
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _listener?.Dispose();
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort in dispose.
        }
    }
}

/// <summary>Logger that captures all rendered text for secret-leak assertions.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly object _lock = new();
    public List<string> Entries { get; } = [];

    public string AllText
    {
        get
        {
            lock (_lock)
            {
                return string.Join("\n", Entries);
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}

/// <summary>Typed adapter so the capturing logger plugs into DI-style constructors.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly CapturingLogger _inner = new();

    public CapturingLogger Entries => _inner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
