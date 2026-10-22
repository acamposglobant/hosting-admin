using HostingAdmin.Core.SystemInfo;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Disk usage from /proc/mounts (filtered to real filesystems) + libc statvfs
/// (plan §3.6). Read-only; no shell. A mount whose statvfs call fails is
/// reported with null usage fields rather than failing the whole report.
/// </summary>
public sealed class LinuxDiskProvider : IDiskProvider
{
    private const string MountsPath = "/proc/mounts";

    public async Task<DiskSnapshot> GetDisksAsync(CancellationToken cancellationToken)
    {
        string? mountsContent;
        try
        {
            mountsContent = await File.ReadAllTextAsync(MountsPath, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FileNotFoundException
                or DirectoryNotFoundException or System.Security.SecurityException)
        {
            return DiskSnapshot.Unavailable("disk_source_unavailable");
        }

        var mounts = MountsParser.Parse(mountsContent);
        var disks = new List<DiskUsage>();

        // Deduplicate by device: the same device can be bind-mounted or mounted
        // at several paths; the first mount point is reported.
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (!seenDevices.Add(mount.Device))
            {
                continue;
            }

            var usage = StatFs.GetUsage(mount.MountPoint);
            if (usage is null || usage.TotalBytes <= 0)
            {
                continue;
            }

            var usedBytes = usage.TotalBytes - usage.FreeBytes;
            var usagePercent = usage.TotalBytes > 0
                ? 100.0 * usedBytes / usage.TotalBytes
                : 0;

            disks.Add(new DiskUsage(
                Device: mount.Device,
                MountPoint: mount.MountPoint,
                FileSystem: mount.FileSystem,
                TotalBytes: usage.TotalBytes,
                UsedBytes: usedBytes,
                AvailableBytes: usage.AvailableBytes,
                UsagePercent: usagePercent));
        }

        return new DiskSnapshot(IsAvailable: true, UnavailableReason: null, disks);
    }
}
