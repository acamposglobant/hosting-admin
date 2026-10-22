using HostingAdmin.Core.Audit;

namespace HostingAdmin.Core.Audit;

/// <summary>
/// Append-only audit store (plan §4). Implementations must use parameterized
/// queries, redact secret material from detail fields, and never expose
/// update/delete paths for individual events.
/// </summary>
public interface IAuditStore
{
    /// <summary>Appends an audit event. The detail is scrubbed of secret material before persistence.</summary>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of audit events, newest first (plan §4: bounded pagination,
    /// <paramref name="take"/> capped by the caller at 200). Parameterized; read-only.
    /// </summary>
    Task<AuditPage> QueryAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes audit events older than the retention window and returns the
    /// number of rows removed. The only deletion path for audit data.
    /// </summary>
    Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}
