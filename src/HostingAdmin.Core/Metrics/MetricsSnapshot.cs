namespace HostingAdmin.Core.Metrics;

/// <summary>CPU metrics. <see cref="UsagePercent"/> is null until two samples exist.</summary>
public sealed record CpuMetrics(
    double? UsagePercent,
    int? Cores,
    double? LoadAverage1m,
    double? LoadAverage5m,
    double? LoadAverage15m,
    DateTime SampledAt);

/// <summary>Memory metrics in bytes; null when the source is unavailable.</summary>
public sealed record MemoryMetrics(
    long? TotalBytes,
    long? UsedBytes,
    long? AvailableBytes,
    double? UsagePercent);

/// <summary>Swap metrics in bytes; null when the source is unavailable.</summary>
public sealed record SwapMetrics(
    long? TotalBytes,
    long? UsedBytes,
    double? UsagePercent);

/// <summary>
/// A metrics snapshot (plan §4). When the collection source is unavailable
/// (e.g. Windows development, missing procfs), <see cref="IsAvailable"/> is
/// false with a reason — the endpoint reports the structured "unavailable"
/// shape instead of failing (D11).
/// </summary>
public sealed record MetricsSnapshot(
    bool IsAvailable,
    string? UnavailableReason,
    CpuMetrics? Cpu,
    MemoryMetrics? Memory,
    SwapMetrics? Swap,
    double? UptimeSeconds)
{
    public static MetricsSnapshot Unavailable(string reason) => new(false, reason, null, null, null, null);
}
