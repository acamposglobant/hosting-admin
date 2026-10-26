namespace HostingAdmin.Worker;

/// <summary>
/// Audit shipping configuration (increment 3, D26). Shipping is config-gated
/// off by default: disabled means no worker activity and no syslog writes.
/// </summary>
public sealed class AuditShippingOptions
{
    public const string SectionName = "AuditShipping";

    /// <summary>Master switch. Default false — shipping is opt-in (D26, TA19).</summary>
    public bool Enabled { get; init; }

    /// <summary>Poll interval in seconds (default 10). Minimum 1.</summary>
    public int PollIntervalSeconds { get; init; } = 10;

    /// <summary>Maximum events shipped per poll cycle (bounded work per tick).</summary>
    public int BatchSize { get; init; } = 100;
}
