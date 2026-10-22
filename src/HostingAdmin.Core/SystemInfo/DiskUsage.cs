namespace HostingAdmin.Core.SystemInfo;

/// <summary>Usage of one mounted real filesystem, in bytes.</summary>
public sealed record DiskUsage(
    string Device,
    string MountPoint,
    string FileSystem,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    double UsagePercent);

/// <summary>
/// Disk usage report (plan §4): real filesystems only — pseudo-filesystems
/// (proc, sysfs, tmpfs, cgroup2, ...) are excluded. When the source is
/// unavailable (e.g. Windows development) the structured "unavailable" shape
/// is returned instead of failing (D11).
/// </summary>
public sealed record DiskSnapshot(
    bool IsAvailable,
    string? UnavailableReason,
    IReadOnlyList<DiskUsage> Disks)
{
    public static DiskSnapshot Unavailable(string reason) =>
        new(false, reason, Array.Empty<DiskUsage>());
}
