namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// Audit retention options (plan D9): 90 days default, operator-configurable.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Retention window in days. Default 90 (D9).</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>Interval in hours between retention prune runs.</summary>
    public int PruneIntervalHours { get; init; } = 24;
}
