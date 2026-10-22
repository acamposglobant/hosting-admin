using System.Runtime.InteropServices;
using HostingAdmin.Core.SystemInfo;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Reads OS information from fixed, known-safe sources (plan §3.6, D11):
/// /etc/os-release for the distribution name, /proc/sys/kernel/osrelease for
/// the kernel version, Environment.MachineName for the hostname and
/// RuntimeInformation for the architecture. No shell, no process spawning.
/// </summary>
public sealed class LinuxSystemInfoProvider : ISystemInfoProvider
{
    private const string OsReleasePath = "/etc/os-release";
    private const string KernelReleasePath = "/proc/sys/kernel/osrelease";

    public async Task<SystemInfoSnapshot> GetInfoAsync(CancellationToken cancellationToken)
    {
        var osReleaseContent = await ReadFileAsync(OsReleasePath, cancellationToken);
        var kernelContent = await ReadFileAsync(KernelReleasePath, cancellationToken);

        if (osReleaseContent is null && kernelContent is null)
        {
            return SystemInfoSnapshot.Unavailable("system_info_source_unavailable");
        }

        var osRelease = osReleaseContent is null ? null : OsReleaseParser.Parse(osReleaseContent);

        return new SystemInfoSnapshot(
            IsAvailable: true,
            UnavailableReason: null,
            Hostname: Environment.MachineName,
            OperatingSystem: osRelease?.Name,
            KernelVersion: kernelContent?.Trim(),
            Architecture: RuntimeInformation.ProcessArchitecture.ToString());
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FileNotFoundException
                or DirectoryNotFoundException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
