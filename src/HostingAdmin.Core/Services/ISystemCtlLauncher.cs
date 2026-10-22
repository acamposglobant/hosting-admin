using System.Diagnostics;

namespace HostingAdmin.Core.Services;

/// <summary>
/// Abstraction over the systemctl process spawn (plan T21/T22). The only
/// execution point of the whole application; implementations must use fixed
/// argv (ArgumentList), UseShellExecute=false and an absolute path.
/// </summary>
public interface ISystemCtlLauncher
{
    /// <summary>True when the systemctl binary exists (capability check, D11).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts systemctl with the given argument list. The returned task yields
    /// stdout, or null when the process could not be started (e.g. binary
    /// missing). Throws OperationCanceledException on timeout/cancellation.
    /// </summary>
    Task<string?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Real launcher: Process.Start with ArgumentList, UseShellExecute=false and a
/// hard timeout that kills the process (plan D12, T22).
/// </summary>
public sealed class SystemCtlLauncher : ISystemCtlLauncher
{
    private readonly string _systemctlPath;

    public SystemCtlLauncher(string systemctlPath)
    {
        _systemctlPath = systemctlPath;
        IsAvailable = File.Exists(systemctlPath);
    }

    /// <summary>True when the systemctl binary exists (capability check, D11).</summary>
    public bool IsAvailable { get; }

    public async Task<string?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _systemctlPath, // absolute path (T22)
            UseShellExecute = false,   // never a shell (T22)
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument); // fixed argv, no quoting issues (T22)
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return await stdoutTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Hard timeout: kill the process tree member and rethrow.
            try { process.Kill(entireProcessTree: true); } catch (SystemException) { /* already gone */ }
            throw;
        }
    }
}
