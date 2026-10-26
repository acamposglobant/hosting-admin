using HostingAdmin.Core.Audit;

namespace HostingAdmin.Worker;

/// <summary>
/// Persists the audit shipping cursor (last shipped audit_events id) in the
/// admin_state table (increment 3, D26), so shipping resumes after restart
/// without duplication or loss (TA15).
/// </summary>
public interface IAuditShippingCursorStore
{
    /// <summary>Returns the last shipped audit id, or 0 when nothing was shipped yet.</summary>
    Task<long> GetLastShippedIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the last shipped audit id (idempotent upsert).</summary>
    Task SetLastShippedIdAsync(long id, CancellationToken cancellationToken = default);
}
