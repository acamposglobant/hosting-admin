namespace HostingAdmin.Infrastructure.Linux;

/// <summary>One parsed mount entry from /proc/mounts.</summary>
public sealed record MountEntry(string Device, string MountPoint, string FileSystem);

/// <summary>
/// Defensive parser for /proc/mounts (plan §3.6): filters to real filesystems
/// via an allowlist — pseudo-filesystems (proc, sysfs, tmpfs, cgroup2, squashfs,
/// ...) are excluded by default, which is safer than trying to enumerate every
/// pseudo type. Malformed lines are skipped. Mount points are unescaped
/// (octal \0NN sequences used by procfs for spaces and special characters).
/// </summary>
public static class MountsParser
{
    /// <summary>
    /// Real filesystem allowlist. Anything not listed (proc, sysfs, tmpfs,
    /// devtmpfs, cgroup2, squashfs, overlay, fuse*, ...) is treated as a
    /// pseudo-filesystem and excluded.
    /// </summary>
    private static readonly HashSet<string> RealFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs",
        "vfat", "exfat", "ntfs", "ntfs3", "udf", "iso9660",
    };

    public static IReadOnlyList<MountEntry> Parse(string content)
    {
        var mounts = new List<MountEntry>();
        foreach (var line in content.Split('\n'))
        {
            // Format: device mountpoint fstype options dump pass.
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 ||
                !RealFileSystems.Contains(fields[2]))
            {
                continue;
            }

            mounts.Add(new MountEntry(
                Device: fields[0],
                MountPoint: Unescape(fields[1]),
                FileSystem: fields[2]));
        }

        return mounts;
    }

    /// <summary>Decodes procfs octal escapes (e.g. \040 for space) in mount paths.</summary>
    public static string Unescape(string path)
    {
        if (!path.Contains('\\'))
        {
            return path;
        }

        var builder = new System.Text.StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == '\\' &&
                index + 3 < path.Length &&
                path[index + 1] == '0' &&
                IsOctalDigit(path[index + 2]) &&
                IsOctalDigit(path[index + 3]))
            {
                builder.Append((char)((path[index + 2] - '0') * 8 + (path[index + 3] - '0')));
                index += 3;
            }
            else
            {
                builder.Append(path[index]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctalDigit(char character) =>
        character is >= '0' and <= '7';
}
