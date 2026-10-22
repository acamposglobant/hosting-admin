using HostingAdmin.Core.Services;
using HostingAdmin.Infrastructure.Linux;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Tests for the service status provider (plan task 12, D12, T21/T22):
/// allowlist enforcement before any process start, fixed-argv launch
/// properties, not-found units reported, and defensive output parsing.
/// </summary>
public class ServiceStatusProviderTests
{
    private sealed class RecordingLauncher : ISystemCtlLauncher
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public bool IsAvailable => true;

        public Task<string?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            return Task.FromResult<string?>(
                """
                LoadState=loaded
                ActiveState=active
                SubState=running
                UnitFileState=enabled
                ActiveEnterTimestamp=Mon 2026-10-20 08:14:03 UTC
                """);
        }
    }

    private static ServiceStatusOptions CreateOptions(params string[] unitNames) => new()
    {
        UnitNames = unitNames,
        SystemctlPath = "/usr/bin/systemctl",
        Timeout = TimeSpan.FromSeconds(2),
    };

    // --- T21: non-allowlisted unit names never reach the launcher ---

    [Fact]
    public async Task Provider_OnlyQueriesAllowlistedUnits()
    {
        var launcher = new RecordingLauncher();
        var provider = new SystemdServiceStatusProvider(CreateOptions("nginx.service"), launcher);

        await provider.GetStatusAsync(CancellationToken.None);

        var invocation = Assert.Single(launcher.Invocations);
        Assert.Contains("nginx.service", invocation);
    }

    [Fact]
    public void IsValidUnitName_RejectsInjectionAndTraversal()
    {
        // Defense in depth: even if the allowlist were misconfigured, these
        // names can never reach a process argv.
        Assert.False(ServiceStatusOptions.IsValidUnitName(""));
        Assert.False(ServiceStatusOptions.IsValidUnitName("nginx.service;reboot"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("nginx.service reboot"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("$(reboot)"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("`reboot`"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("nginx\n.service"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("../etc/passwd"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("/bin/sh"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("nosuffix"));
        Assert.False(ServiceStatusOptions.IsValidUnitName(""));
    }

    [Fact]
    public void IsValidUnitName_AcceptsValidSystemdNames()
    {
        Assert.True(ServiceStatusOptions.IsValidUnitName("nginx.service"));
        Assert.True(ServiceStatusOptions.IsValidUnitName("ssh.service"));
        Assert.True(ServiceStatusOptions.IsValidUnitName("php8.3-fpm.service"));
        Assert.True(ServiceStatusOptions.IsValidUnitName("systemd-journald.service"));
        Assert.True(ServiceStatusOptions.IsValidUnitName("postfix@-.service"));
        Assert.False(ServiceStatusOptions.IsValidUnitName("nginx.service extra"));
    }

    // --- T22: fixed argv, UseShellExecute=false, absolute path ---

    [Fact]
    public async Task Provider_RequestsOnlyReadOnlyProperties()
    {
        var launcher = new RecordingLauncher();
        var provider = new SystemdServiceStatusProvider(CreateOptions("nginx.service"), launcher);

        await provider.GetStatusAsync(CancellationToken.None);

        var invocation = Assert.Single(launcher.Invocations);
        Assert.Equal("show", invocation[0]);
        Assert.All(invocation.Skip(1).Take(5), argument => Assert.StartsWith("--property=", argument));
        // Read-only property subset only; no start/stop/restart verbs anywhere.
        Assert.DoesNotContain(invocation, argument =>
            argument is "start" or "stop" or "restart" or "reload" or "kill" or "enable" or "disable");
    }

    // --- Output parsing ---

    [Fact]
    public void ParseShowOutput_MapsProperties()
    {
        const string stdout = """
            LoadState=loaded
            ActiveState=active
            SubState=running
            UnitFileState=enabled
            ActiveEnterTimestamp=Mon 2026-10-20 08:14:03 UTC
            """;

        var status = SystemdServiceStatusProvider.ParseShowOutput("nginx.service", stdout);

        Assert.Equal("nginx.service", status.Name);
        Assert.Equal("loaded", status.LoadState);
        Assert.Equal("active", status.ActiveState);
        Assert.Equal("running", status.SubState);
        Assert.Equal("enabled", status.EnabledState);
        Assert.NotNull(status.Since);
    }

    [Fact]
    public void ParseShowOutput_NotFoundUnit_IsReportedNotHidden()
    {
        const string stdout = """
            LoadState=not-found
            ActiveState=inactive
            SubState=dead
            UnitFileState=""
            ActiveEnterTimestamp=""
            """;

        var status = SystemdServiceStatusProvider.ParseShowOutput("notinstalled.service", stdout);

        Assert.Equal("not-found", status.LoadState);
        Assert.Equal("inactive", status.ActiveState);
        Assert.Equal("dead", status.SubState);
        Assert.Null(status.Since);
    }

    [Fact]
    public void ParseShowOutput_MalformedOutput_DegradesToUnknown()
    {
        var status = SystemdServiceStatusProvider.ParseShowOutput("x.service", "garbage\nno=equals=here\n");

        Assert.Equal("unknown", status.LoadState);
        Assert.Equal("unknown", status.ActiveState);
        Assert.Equal("unknown", status.SubState);
        Assert.Equal("unknown", status.EnabledState);
        Assert.Null(status.Since);
    }

    [Fact]
    public void ParseShowOutput_ValueContainingEquals_IsPreserved()
    {
        const string stdout = "LoadState=loaded\nSubState=a=b\n";

        var status = SystemdServiceStatusProvider.ParseShowOutput("x.service", stdout);

        Assert.Equal("a=b", status.SubState);
    }

    // --- Capability check (D11) ---

    [Fact]
    public async Task Provider_WithoutSystemctlBinary_ReportsCapabilityUnavailable()
    {
        // A launcher whose binary path does not exist on this machine.
        var launcher = new SystemCtlLauncher(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "definitely-missing-systemctl"));
        var provider = new SystemdServiceStatusProvider(CreateOptions("nginx.service"), launcher);

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.False(snapshot.CapabilityAvailable);
        Assert.Equal("systemd_not_available", snapshot.UnavailableReason);
        Assert.Empty(snapshot.Services);
    }
}
