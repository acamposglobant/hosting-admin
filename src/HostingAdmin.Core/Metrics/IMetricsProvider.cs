namespace HostingAdmin.Core.Metrics;

/// <summary>
/// Provides a point-in-time system metrics snapshot (plan §3.6). Implementations
/// read only fixed, known-safe sources; they never enumerate processes or spawn
/// processes. A snapshot is always returned: when the platform cannot supply
/// metrics the result is the structured "unavailable" shape (D11).
/// </summary>
public interface IMetricsProvider
{
    Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
