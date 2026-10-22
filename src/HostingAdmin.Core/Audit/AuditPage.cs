namespace HostingAdmin.Core.Audit;

/// <summary>
/// One page of audit events (plan §4: bounded pagination, take capped at 200).
/// Items are ordered newest first.
/// </summary>
public sealed record AuditPage(
    IReadOnlyList<AuditEvent> Items,
    int TotalCount);
