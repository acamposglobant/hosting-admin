using System.Diagnostics;
using System.Text;

namespace HostingAdmin.Agent;

/// <summary>Result of running the systemctl child process (D19).</summary>
public sealed record SystemctlResult(
    bool TimedOut,
    int? ExitCode,
    string Output)
{
    /// <summary>Exit code 0 — the action succeeded.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs the fixed systemctl command for an allowlisted unit (D19).
///
/// Command construction rules (no shell anywhere):
/// - <see cref="System.Diagnostics.ProcessStartInfo.UseShellExecute"/> is false.
/// - Arguments are passed via <c>ArgumentList</c> — never interpolated into a
///   command string.
/// - The executable path comes from validated configuration, never from input.
/// - Exactly three argv entries: systemctl, action, unit.
/// - Hard timeout: on expiry the child tree is killed and a structured
///   timeout result is returned.
/// </summary>
public interface ISystemctlRunner
{
    Task<SystemctlResult> RunAsync(string action, string unit, CancellationToken cancellationToken);
}

public sealed class SystemctlRunner : ISystemctlRunner
{
    private readonly AgentOptions _options;
    private readonly ILogger<SystemctlRunner> _logger;

    public SystemctlRunner(AgentOptions options, ILogger<SystemctlRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Builds the fixed argv for a service action (D19). Static and pure so
    /// argument construction is testable on every platform.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string action, string unit) =>
        [action, unit];

    public async Task<SystemctlResult> RunAsync(string action, string unit, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.SystemctlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Fixed argv: exactly the verb and the (already allowlist-validated)
        // unit name. No shell, no extra arguments (D19).
        startInfo.ArgumentList.Add(action);
        startInfo.ArgumentList.Add(unit);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            return new SystemctlResult(TimedOut: false, ExitCode: null, Output: "Failed to start systemctl.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutMs = _options.CommandTimeoutSeconds * 1000;
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Hard timeout (D19): kill the whole child tree, report failure.
            TryKill(process);
            return new SystemctlResult(
                TimedOut: true, ExitCode: null,
                Output: Truncate($"Command timed out after {_options.CommandTimeoutSeconds}s."));
        }
        catch (OperationCanceledException)
        {
            // Request-level cancellation (socket timeout or shutdown): the
            // child must never outlive the request (D19).
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);
        var combined = new StringBuilder().Append(stderr).Append(stdout);
        var output = Truncate(combined.ToString().Trim());
        _logger.LogInformation(
            "systemctl {Action} {Unit} exited with {ExitCode}", action, unit, process.ExitCode);
        return new SystemctlResult(TimedOut: false, ExitCode: process.ExitCode, Output: output);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }

    private string Truncate(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…[truncated]";
}
