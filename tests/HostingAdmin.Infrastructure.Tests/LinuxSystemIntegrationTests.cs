using System.Runtime.InteropServices;
using HostingAdmin.Infrastructure.Linux;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Linux integration tests for the task 11 providers (plan T26): run in full on
/// the Linux CI runner, no-op on Windows dev machines (Category=LinuxIntegration
/// convention, see LinuxIntegrationSmokeTests).
/// </summary>
public class LinuxSystemIntegrationTests
{
    [Fact]
    [Trait("Category", "LinuxIntegration")]
    public async Task LinuxSystemInfoProvider_OnLinux_ReturnsRealValues()
    {
        if (!RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            return;
        }

        var provider = new LinuxSystemInfoProvider();
        var info = await provider.GetInfoAsync(CancellationToken.None);

        Assert.True(info.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(info.Hostname));
        Assert.False(string.IsNullOrWhiteSpace(info.OperatingSystem));
        Assert.False(string.IsNullOrWhiteSpace(info.KernelVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.Architecture));
    }

    [Fact]
    [Trait("Category", "LinuxIntegration")]
    public async Task LinuxDiskProvider_OnLinux_ExcludesPseudoFilesystems_AndReportsRoot()
    {
        if (!RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            return;
        }

        var provider = new LinuxDiskProvider();
        var snapshot = await provider.GetDisksAsync(CancellationToken.None);

        Assert.True(snapshot.IsAvailable);
        Assert.NotEmpty(snapshot.Disks);

        // The root filesystem is a real filesystem and must be present.
        var root = Assert.Single(snapshot.Disks, disk => disk.MountPoint == "/");
        Assert.True(root.TotalBytes > 0);
        Assert.True(root.UsedBytes >= 0);
        Assert.True(root.UsagePercent is >= 0 and <= 100);

        // Pseudo-filesystems must never appear.
        Assert.DoesNotContain(snapshot.Disks, disk => disk.FileSystem is "proc" or "sysfs" or "tmpfs" or "cgroup2" or "squashfs");
    }
}
