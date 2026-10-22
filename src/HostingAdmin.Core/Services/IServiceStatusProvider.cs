namespace HostingAdmin.Core.Services;

/// <summary>
/// Read-only systemd service status provider (plan §3.6, D12). Unit names come
/// only from the server-side allowlist; the client never supplies names.
/// </summary>
public interface IServiceStatusProvider
{
    Task<ServicesSnapshot> GetStatusAsync(CancellationToken cancellationToken);
}
