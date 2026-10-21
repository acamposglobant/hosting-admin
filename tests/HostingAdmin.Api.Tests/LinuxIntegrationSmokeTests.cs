using System.Runtime.InteropServices;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Linux integration trait smoke test (plan task 3 / T26).
/// Establishes the Category=LinuxIntegration convention: tests tagged with this
/// trait run in full on the Linux CI runner and are excluded from Windows CI runs
/// via the Category!=LinuxIntegration filter.
///
/// On non-Linux hosts this test returns immediately (no-op) so local Windows
/// development and portable test runs stay green. On Linux it verifies that the
/// procfs surface the metrics providers (task 10) will rely on is actually
/// readable in the CI environment.
/// </summary>
public class LinuxIntegrationSmokeTests
{
    [Fact]
    [Trait("Category", "LinuxIntegration")]
    public void Procfs_IsReadable_OnLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Not a Linux host (e.g. Windows dev machine): nothing to verify.
            return;
        }

        var statusPath = "/proc/self/status";
        Assert.True(File.Exists(statusPath), $"/proc is expected to exist on Linux but {statusPath} was not found.");

        var content = File.ReadAllText(statusPath);
        Assert.Contains("Name:", content);
    }
}
