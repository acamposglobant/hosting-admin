using HostingAdmin.Infrastructure.Linux;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Fixture tests for the task 11 parsers: /etc/os-release and /proc/mounts
/// (plan §3.6). Malformed content degrades to null/empty rather than throwing.
/// </summary>
public class SystemInfoParserTests
{
    // --- /etc/os-release ---

    [Fact]
    public void OsReleaseParser_ParsesPrettyName_AndVersion()
    {
        const string content = """
            NAME="Ubuntu"
            VERSION_ID="24.04"
            PRETTY_NAME="Ubuntu 24.04.1 LTS"
            ID=ubuntu
            """;

        var info = OsReleaseParser.Parse(content);

        Assert.NotNull(info);
        Assert.Equal("Ubuntu 24.04.1 LTS", info!.Name);
        Assert.Equal("24.04", info.Version);
    }

    [Fact]
    public void OsReleaseParser_SingleQuotedValues_AreUnquoted()
    {
        const string content = "NAME='Debian GNU/Linux'\nPRETTY_NAME='Debian 12'\n";

        var info = OsReleaseParser.Parse(content);

        Assert.NotNull(info);
        Assert.Equal("Debian 12", info!.Name);
    }

    [Fact]
    public void OsReleaseParser_MissingPrettyName_FallsBackToName()
    {
        const string content = "NAME=\"Alpine Linux\"\nID=alpine\n";

        var info = OsReleaseParser.Parse(content);

        Assert.NotNull(info);
        Assert.Equal("Alpine Linux", info!.Name);
        Assert.Null(info.Version);
    }

    [Fact]
    public void OsReleaseParser_MalformedContent_ReturnsNull()
    {
        Assert.Null(OsReleaseParser.Parse("garbage\nno equals signs here\n"));
        Assert.Null(OsReleaseParser.Parse(""));
    }

    // --- /proc/mounts ---

    [Fact]
    public void MountsParser_RealFilesystems_Included()
    {
        const string content = """
            /dev/sda1 / ext4 rw,relatime 0 1
            /dev/sdb1 /data xfs rw,relatime,attr2 0 2
            /dev/nvme0n1p2 /boot vfat rw,relatime,fmask=0077 0 0
            """;

        var mounts = MountsParser.Parse(content);

        Assert.Equal(3, mounts.Count);
        Assert.Equal("/dev/sda1", mounts[0].Device);
        Assert.Equal("/", mounts[0].MountPoint);
        Assert.Equal("ext4", mounts[0].FileSystem);
        Assert.Equal("/data", mounts[1].MountPoint);
    }

    [Fact]
    public void MountsParser_PseudoFilesystems_Excluded()
    {
        const string content = """
            proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
            sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
            tmpfs /run tmpfs rw,nosuid,nodev 0 0
            devtmpfs /dev devtmpfs rw,relatime 0 0
            cgroup2 /sys/fs/cgroup cgroup2 rw 0 0
            /dev/sda1 / ext4 rw,relatime 0 1
            /dev/loop0 /snap/core20 squashfs ro,nodev 0 0
            overlay /var/lib/docker/overlay2/abc/merged overlay rw 0 0
            """;

        var mounts = MountsParser.Parse(content);

        var single = Assert.Single(mounts);
        Assert.Equal("/dev/sda1", single.Device);
        Assert.Equal("ext4", single.FileSystem);
    }

    [Fact]
    public void MountsParser_MalformedLines_Skipped()
    {
        const string content = """
            onlyonefield
            /dev/sda1 /
            /dev/sdb1 /data xfs rw 0 0
            """;

        var mounts = MountsParser.Parse(content);

        // Lines with fewer than 3 fields (device, mountpoint, fstype) are
        // skipped; a minimal 3-field line is still usable.
        var single = Assert.Single(mounts);
        Assert.Equal("/dev/sdb1", single.Device);
        Assert.Equal("xfs", single.FileSystem);
    }

    [Fact]
    public void MountsParser_OctalEscapes_AreUnescaped()
    {
        // \040 is space, \011 is tab in procfs mount paths.
        Assert.Equal("/mnt/my data", MountsParser.Unescape("/mnt/my\\040data"));
        Assert.Equal("/mnt/a\tb", MountsParser.Unescape("/mnt/a\\011b"));
        Assert.Equal("/plain", MountsParser.Unescape("/plain"));
    }

    [Fact]
    public void MountsParser_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(MountsParser.Parse(""));
    }
}
