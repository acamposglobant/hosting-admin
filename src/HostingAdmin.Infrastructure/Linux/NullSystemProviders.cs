using HostingAdmin.Core.SystemInfo;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Windows/development stub (plan §3.6): returns the structured "unavailable"
/// shape instead of system info. Registered when the platform has no
/// /etc/os-release (D11).
/// </summary>
public sealed class NullSystemInfoProvider : ISystemInfoProvider
{
    public Task<SystemInfoSnapshot> GetInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SystemInfoSnapshot.Unavailable("system_info_source_unavailable"));
}

/// <summary>
/// Windows/development stub (plan §3.6): returns the structured "unavailable"
/// shape instead of disk usage. Registered when the platform has no
/// /proc/mounts (D11).
/// </summary>
public sealed class NullDiskProvider : IDiskProvider
{
    public Task<DiskSnapshot> GetDisksAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DiskSnapshot.Unavailable("disk_source_unavailable"));
}
