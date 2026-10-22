using System.Globalization;
using HostingAdmin.Core.Services;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Read-only systemd status provider (plan §3.6, D12): runs `systemctl show`
/// with fixed argv for each allowlisted unit and parses the key=value output.
/// A unit that is not installed is reported with loadState "not-found", never
/// hidden. Capability check (D11): systemctl must exist at the configured
/// absolute path.
/// </summary>
public sealed class SystemdServiceStatusProvider : IServiceStatusProvider
{
    /// <summary>Properties requested from systemctl show (read-only subset).</summary>
    private static readonly string[] RequestedProperties =
    [
        "LoadState",
        "ActiveState",
        "SubState",
        "UnitFileState",
        "ActiveEnterTimestamp",
    ];

    private readonly ServiceStatusOptions _options;
    private readonly ISystemCtlLauncher _launcher;

    public SystemdServiceStatusProvider(ServiceStatusOptions options, ISystemCtlLauncher launcher)
    {
        _options = options;
        _launcher = launcher;
    }

    public async Task<ServicesSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!_launcher.IsAvailable)
        {
            return new ServicesSnapshot(
                CapabilityAvailable: false,
                UnavailableReason: "systemd_not_available",
                Services: []);
        }

        var services = new List<ServiceStatus>(_options.UnitNames.Count);
        foreach (var unitName in _options.UnitNames)
        {
            services.Add(await QueryUnitAsync(unitName, cancellationToken).ConfigureAwait(false));
        }

        return new ServicesSnapshot(
            CapabilityAvailable: true,
            UnavailableReason: null,
            Services: services);
    }

    private async Task<ServiceStatus> QueryUnitAsync(string unitName, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "show" };
        foreach (var property in RequestedProperties)
        {
            arguments.Add($"--property={property}");
        }
        arguments.Add("--");
        arguments.Add(unitName);

        string? stdout;
        try
        {
            stdout = await _launcher.RunAsync(arguments, _options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SystemException)
        {
            // Operational failure of one query degrades that unit only.
            return new ServiceStatus(
                Name: unitName,
                LoadState: "unknown",
                ActiveState: "unknown",
                SubState: "unknown",
                EnabledState: "unknown",
                Since: null);
        }

        if (stdout is null)
        {
            return new ServiceStatus(unitName, "unknown", "unknown", "unknown", "unknown", null);
        }

        return ParseShowOutput(unitName, stdout);
    }

    /// <summary>Parses `systemctl show --property=...` key=value output.</summary>
    public static ServiceStatus ParseShowOutput(string unitName, string stdout)
    {
        string? loadState = null, activeState = null, subState = null, enabledState = null, activeEnterTimestamp = null;
        foreach (var line in stdout.Split('\n'))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            switch (key)
            {
                case "LoadState": loadState = value; break;
                case "ActiveState": activeState = value; break;
                case "SubState": subState = value; break;
                case "UnitFileState": enabledState = value; break;
                case "ActiveEnterTimestamp": activeEnterTimestamp = value; break;
            }
        }

        DateTimeOffset? since = null;
        // systemctl reports "Mon 2026-10-20 08:14:03 UTC" (C locale). The
        // day-name prefix is redundant (and not parseable with "ddd"); strip
        // it and parse the remainder. Anything else degrades to null.
        var timestamp = activeEnterTimestamp;
        if (timestamp is { Length: > 4 } && timestamp[3] == ' ')
        {
            timestamp = timestamp[4..];
        }

        if (!string.IsNullOrEmpty(timestamp) &&
            DateTimeOffset.TryParseExact(
                timestamp,
                "yyyy-MM-dd HH:mm:ss 'UTC'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            since = parsed;
        }

        return new ServiceStatus(
            Name: unitName,
            LoadState: loadState ?? "unknown",
            ActiveState: activeState ?? "unknown",
            SubState: subState ?? "unknown",
            EnabledState: enabledState ?? "unknown",
            Since: since);
    }
}
