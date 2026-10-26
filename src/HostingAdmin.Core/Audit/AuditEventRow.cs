namespace HostingAdmin.Core.Audit;

/// <summary>
/// An audit event as stored, including its monotonically increasing row id.
/// Used by the shipping path (increment 3, D26) to track a resumable cursor.
/// </summary>
public sealed record AuditEventRow
{
    public required long Id { get; init; }
    public required AuditEvent Event { get; init; }
}
