using System.Diagnostics;
using HostingAdmin.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Real process execution tests (TA4): fixed argv, no shell, hard timeout
/// with tree kill. These run a platform-appropriate harmless child process —
/// on Linux a shell script shim standing in for systemctl; skipped on Windows
/// (covered by the Linux CI job).
/// </summary>
public class SystemctlRunnerTests
{
    private static bool OnLinux => OperatingSystem.IsLinux();

    private static AgentOptions Options(string shimPath, int timeoutSeconds = 10) => new()
    {
        SocketPath = "/run/hostingadmin/agent.sock",
        SecretFile = "/etc/hostingadmin/agent-secret",
        SystemctlPath = shimPath,
        CommandTimeoutSeconds = timeoutSeconds,
    };

    [Fact]
    public async Task Runner_executes_fixed_argv_and_maps_exit_code()
    {
        if (!OnLinux)
        {
            return;
        }

        // Shim: succeeds and prints to stderr+stdout (systemctl writes to both).
        var shim = WriteShim("#!/bin/sh\necho \"some stdout\"\necho \"some stderr\" >&2\nexit 0\n");
        try
        {
            var runner = new SystemctlRunner(Options(shim), NullLogger<SystemctlRunner>.Instance);
            var result = await runner.RunAsync("restart", "nginx.service", CancellationToken.None);

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Succeeded);
            Assert.Contains("some stderr", result.Output);
            Assert.Contains("some stdout", result.Output);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Fact]
    public async Task Runner_maps_nonzero_exit_code_to_failure()
    {
        if (!OnLinux)
        {
            return;
        }

        var shim = WriteShim("#!/bin/sh\necho \"unit not found\" >&2\nexit 5\n");
        try
        {
            var runner = new SystemctlRunner(Options(shim), NullLogger<SystemctlRunner>.Instance);
            var result = await runner.RunAsync("start", "nginx.service", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(5, result.ExitCode);
            Assert.Contains("unit not found", result.Output);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Fact]
    public async Task Runner_timeout_kills_child_and_returns_structured_failure()
    {
        if (!OnLinux)
        {
            return;
        }

        // Shim: sleeps far longer than the 1s test timeout.
        var shim = WriteShim("#!/bin/sh\nsleep 30\n");
        try
        {
            var runner = new SystemctlRunner(Options(shim, timeoutSeconds: 1), NullLogger<SystemctlRunner>.Instance);
            var result = await runner.RunAsync("restart", "nginx.service", CancellationToken.None);

            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Contains("timed out", result.Output);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Fact]
    public async Task Runner_truncates_excessive_output()
    {
        if (!OnLinux)
        {
            return;
        }

        var shim = WriteShim("#!/bin/sh\nyes spam | head -c 100000\n");
        try
        {
            var runner = new SystemctlRunner(Options(shim), NullLogger<SystemctlRunner>.Instance);
            var result = await runner.RunAsync("start", "nginx.service", CancellationToken.None);

            Assert.True(result.Output.Length <= 2100);
            Assert.Contains("truncated", result.Output);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    private static string WriteShim(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"systemctl-shim-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, contents);
        MakeExecutable(path);
        return path;
    }

    private static void MakeExecutable(string path)
    {
        var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            ArgumentList = { "+x", path },
            UseShellExecute = false,
        });
        chmod!.WaitForExit();
    }
}
