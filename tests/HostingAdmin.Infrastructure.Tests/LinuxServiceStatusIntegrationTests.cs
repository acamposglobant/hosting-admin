using System.Runtime.InteropServices;
using HostingAdmin.Core.Services;
using HostingAdmin.Infrastructure.Linux;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Linux integration test for the systemctl runner (plan task 12, T26): runs
/// in full on the Linux CI runner, no-op on Windows dev machines.
/// </summary>
public class LinuxServiceStatusIntegrationTests
{
    [Fact]
    [Trait("Category", "LinuxIntegration")]
    public async Task SystemdServiceStatusProvider_OnLinux_ReportsRealUnitState()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // systemd-journald exists on every systemd machine; a unit that is
        // certainly not installed exercises the not-found reporting path.
        var options = new ServiceStatusOptions
        {
            UnitNames = ["systemd-journald.service", "definitely-not-installed-unit.service"],
            SystemctlPath = "/usr/bin/systemctl",
            Timeout = TimeSpan.FromSeconds(5),
        };
        var provider = new SystemdServiceStatusProvider(options, new SystemCtlLauncher(options.SystemctlPath));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.True(snapshot.CapabilityAvailable);
        Assert.Equal(2, snapshot.Services.Count);

        var journald = Assert.Single(snapshot.Services, service => service.Name == "systemd-journald.service");
        Assert.Equal("loaded", journald.LoadState);
        Assert.Equal("active", journald.ActiveState);
        Assert.Equal("running", journald.SubState);

        var missing = Assert.Single(snapshot.Services, service => service.Name == "definitely-not-installed-unit.service");
        Assert.Equal("not-found", missing.LoadState);
    }
}
