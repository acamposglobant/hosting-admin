namespace HostingAdmin.Core.Services;

/// <summary>Status of one systemd unit (plan §4, task 12).</summary>
public sealed record ServiceStatus(
    string Name,
    string LoadState,
    string ActiveState,
    string SubState,
    string EnabledState,
    DateTimeOffset? Since);

/// <summary>
/// Status of the configured systemd units (plan §4). Units come only from the
/// server-side allowlist; a missing unit is reported, not hidden. When systemd
/// is not available (e.g. Windows development) the structured "unavailable"
/// shape is returned (D11).
/// </summary>
public sealed record ServicesSnapshot(
    bool CapabilityAvailable,
    string? UnavailableReason,
    IReadOnlyList<ServiceStatus> Services);
