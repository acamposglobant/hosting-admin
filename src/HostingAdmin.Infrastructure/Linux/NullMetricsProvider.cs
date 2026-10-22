using HostingAdmin.Core.Metrics;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Windows/development stub (plan §3.6): returns the structured "unavailable"
/// shape instead of metrics. Registered when the platform has no procfs (D11).
/// </summary>
public sealed class NullMetricsProvider : IMetricsProvider
{
    public Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(MetricsSnapshot.Unavailable("metrics_source_unavailable"));
}
