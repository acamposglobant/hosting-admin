using System.Runtime.InteropServices;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// P/Invoke binding to libc statvfs (plan §3.6: disks via statvfs, no shell).
/// Read-only usage on fixed mount paths parsed from /proc/mounts. Struct layout
/// matches Linux x86-64/armlinux glibc (unsigned long + fsblkcnt_t = 8 bytes each).
/// </summary>
public static partial class StatFs
{
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StatVfs
    {
        public ulong F_Bsize;
        public ulong F_Frsize;
        public ulong F_Blocks;
        public ulong F_BFree;
        public ulong F_BAvail;
        public ulong F_Files;
        public ulong F_FFree;
        public ulong F_FAvail;
        public ulong F_Fsid;
        public ulong F_Flag;
        public ulong F_Namemax;
        public fixed uint F_Spare[6];
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statvfs(string path, out StatVfs buffer);

    /// <summary>
    /// Reads free/total space for a mount point. Returns null when the call
    /// fails (e.g. permission denied, filesystem vanished) — the caller
    /// degrades that disk instead of failing the whole report.
    /// </summary>
    public static unsafe StatVfsResult? GetUsage(string path)
    {
        StatVfs native;
        var result = statvfs(path, out native);
        if (result != 0)
        {
            return null;
        }

        var blockSize = native.F_Frsize != 0 ? native.F_Frsize : native.F_Bsize;
        if (blockSize == 0)
        {
            return null;
        }

        return new StatVfsResult(
            TotalBytes: checked((long)(native.F_Blocks * blockSize)),
            FreeBytes: checked((long)(native.F_BFree * blockSize)),
            AvailableBytes: checked((long)(native.F_BAvail * blockSize)));
    }
}

public sealed record StatVfsResult(long TotalBytes, long FreeBytes, long AvailableBytes);
