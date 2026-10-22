using System.Collections.Concurrent;

namespace HostingAdmin.Core.Audit;

/// <summary>
/// Selective audit-on-access policy (plan §3.7, D8): routine dashboard polling
/// is not audited, but the <b>first data access per session</b> is. Tracking is
/// in-memory per process lifetime; after a restart a session's first access may
/// be recorded once more, which is acceptable for an audit trail.
/// </summary>
public sealed class SelectiveAuditService
{
    private readonly IAuditStore _auditStore;
    private readonly ConcurrentDictionary<string, byte> _sessionsWithRecordedAccess = new();

    public SelectiveAuditService(IAuditStore auditStore)
    {
        _auditStore = auditStore;
    }

    /// <summary>
    /// Writes a single "data.access" audit row the first time a given session
    /// accesses dashboard data; subsequent polls within the same session write
    /// nothing (T15). Returns true when a row was written.
    /// </summary>
    public async Task<bool> AuditDataAccessIfNeededAsync(
        string sessionIdHash,
        string endpoint,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        if (!_sessionsWithRecordedAccess.TryAdd(sessionIdHash, 0))
        {
            return false;
        }

        await _auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "admin",
            Action = "data.access",
            Outcome = AuditOutcomes.Success,
            SourceIp = sourceIp,
            // Detail records the endpoint, never the session identifier (plan
            // §4: detail must not contain session identifiers or credentials).
            Detail = endpoint,
        }, cancellationToken);
        return true;
    }
}
