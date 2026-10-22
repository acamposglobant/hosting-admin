using System.Collections.Concurrent;

namespace HostingAdmin.Core.Audit;

/// <summary>
/// Failure-event discipline (plan §3.7, T16): repeated identical provider
/// failures are grouped — the audit trail records the <b>state transition into
/// failure</b> and the <b>recovery</b>, not every repeated occurrence.
/// State is in-memory per process lifetime; a restart re-arms the transition
/// detection, which errs on the side of recording (safe for an audit trail).
/// </summary>
public sealed class FailureAuditGrouper
{
    private readonly IAuditStore _auditStore;
    private readonly ConcurrentDictionary<string, byte> _failureKeysInFailureState = new();

    public FailureAuditGrouper(IAuditStore auditStore)
    {
        _auditStore = auditStore;
    }

    /// <summary>
    /// Records a failure only on the transition into the failure state: the
    /// first occurrence, or any occurrence after a recorded recovery. Repeated
    /// identical failures while already in the failure state write nothing.
    /// Returns true when a row was written.
    /// </summary>
    public async Task<bool> RecordFailureAsync(
        string failureKey,
        string action,
        string? detail,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        if (!_failureKeysInFailureState.TryAdd(failureKey, 0))
        {
            return false; // already in failure state: grouped, not recorded
        }

        await _auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "system",
            Action = action,
            Outcome = AuditOutcomes.Failure,
            Detail = detail,
        }, cancellationToken);
        return true;
    }

    /// <summary>
    /// Records a recovery only when the tracked failure key was in the failure
    /// state. Returns true when a row was written.
    /// </summary>
    public async Task<bool> RecordRecoveryAsync(
        string failureKey,
        string action,
        string? detail,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        if (!_failureKeysInFailureState.TryRemove(failureKey, out _))
        {
            return false; // was not in failure state: nothing to recover from
        }

        await _auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "system",
            Action = action,
            Outcome = AuditOutcomes.Success,
            Detail = detail,
        }, cancellationToken);
        return true;
    }
}
