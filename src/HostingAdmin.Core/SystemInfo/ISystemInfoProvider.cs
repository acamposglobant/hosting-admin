namespace HostingAdmin.Core.SystemInfo;

/// <summary>Provides operating system information (plan §4, task 11).</summary>
public interface ISystemInfoProvider
{
    Task<SystemInfoSnapshot> GetInfoAsync(CancellationToken cancellationToken);
}

/// <summary>Provides disk usage for real filesystems (plan §4, task 11).</summary>
public interface IDiskProvider
{
    Task<DiskSnapshot> GetDisksAsync(CancellationToken cancellationToken);
}
