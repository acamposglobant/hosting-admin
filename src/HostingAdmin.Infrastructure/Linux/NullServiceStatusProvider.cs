using HostingAdmin.Core.Services;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Windows/development stub (plan §3.6): returns the structured "unavailable"
/// shape instead of service status. Registered when systemd is not available (D11).
/// </summary>
public sealed class NullServiceStatusProvider : IServiceStatusProvider
{
    public Task<ServicesSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ServicesSnapshot(
            CapabilityAvailable: false,
            UnavailableReason: "systemd_not_available",
            Services: []));
}
