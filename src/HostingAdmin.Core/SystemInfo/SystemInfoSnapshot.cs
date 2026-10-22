namespace HostingAdmin.Core.SystemInfo;

/// <summary>
/// Operating system information (plan §4): hostname, OS, kernel, architecture.
/// Fields are null when the source is unavailable; when the whole source is
/// missing (e.g. Windows development) the structured "unavailable" shape is
/// returned instead of failing (D11).
/// </summary>
public sealed record SystemInfoSnapshot(
    bool IsAvailable,
    string? UnavailableReason,
    string? Hostname,
    string? OperatingSystem,
    string? KernelVersion,
    string? Architecture)
{
    public static SystemInfoSnapshot Unavailable(string reason) =>
        new(false, reason, null, null, null, null);
}
