namespace HostingAdmin.Core.Metrics;

/// <summary>
/// Metrics collection bounds (plan D10): short-TTL cache, per-operation timeout
/// and capped concurrent collections keep dashboard polling cheap and bounded.
/// </summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>How long a snapshot is served from cache before recollecting.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Hard upper bound for a single collection attempt.</summary>
    public TimeSpan CollectionTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
