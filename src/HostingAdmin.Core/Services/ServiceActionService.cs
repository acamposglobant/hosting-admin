using System.Security.Cryptography;
using System.Text;
using HostingAdmin.Core.Agent;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Sessions;

namespace HostingAdmin.Core.Services;

/// <summary>High-level result of a service action request (plan §4 shapes).</summary>
public enum ServiceActionRequestOutcome
{
    /// <summary>Executed directly (start) or via confirmation (stop/restart).</summary>
    Executed,

    /// <summary>Confirmation created (stop/restart) — details in <see cref="ServiceActionRequestResult.Pending"/>.</summary>
    ConfirmationRequired,

    /// <summary>Unit is not on the allowlist; nothing was sent anywhere.</summary>
    UnitNotAllowed,

    /// <summary>Confirmation token unknown, consumed, expired, or session-mismatched.</summary>
    InvalidConfirmation,

    /// <summary>Agent channel is unavailable (D24).</summary>
    CapabilityUnavailable,

    /// <summary>Confirmation discarded (cancel path).</summary>
    Cancelled,
}

/// <summary>Outcome of a direct or confirmed execution.</summary>
public sealed record ServiceActionExecution(
    string Unit,
    string Action,
    AgentOperationOutcome Outcome,
    string Detail,
    DateTime CompletedUtc);

/// <summary>Pending confirmation details returned to the client (plan §4 shape).</summary>
public sealed record ServiceActionPending(
    string ConfirmationId,
    string Unit,
    string Action,
    DateTime ExpiresUtc);

/// <summary>Result returned by <see cref="ServiceActionService"/> operations.</summary>
public sealed record ServiceActionRequestResult(
    ServiceActionRequestOutcome Outcome,
    ServiceActionExecution? Execution = null,
    ServiceActionPending? Pending = null);

/// <summary>
/// Use-case layer for service mutations (plan task 4, D20): API-side allowlist
/// enforcement (D18), direct execution for non-destructive actions, two-step
/// confirmation for destructive ones, and audit for every step (D21).
/// </summary>
public sealed class ServiceActionService(
    ServiceStatusOptions serviceOptions,
    IAgentChannel agentChannel,
    IServiceActionConfirmationStore confirmationStore,
    IAuditStore auditStore,
    FailureAuditGrouper failureGrouper,
    TimeProvider timeProvider)
{
    /// <summary>Confirmation token lifetime (D20: default 5 minutes).</summary>
    public static readonly TimeSpan DefaultConfirmationTtl = TimeSpan.FromMinutes(5);

    /// <summary>Actions that require two-step confirmation (D20).</summary>
    public static readonly IReadOnlyList<string> ConfirmationRequiredActions = ["stop", "restart"];

    /// <summary>All actions the API will ever accept (D23 mirror).</summary>
    public static readonly IReadOnlyList<string> AllowedActions = ["start", "stop", "restart"];

    /// <summary>
    /// Handles a mutation request for <paramref name="unit"/>. Non-destructive
    /// actions execute directly; destructive ones create a confirmation (D20).
    /// </summary>
    public async Task<ServiceActionRequestResult> RequestActionAsync(
        string unit,
        string action,
        string sessionIdHash,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        // API-side allowlist enforcement (D18, TA8): exact match only. A
        // non-allowlisted unit never reaches the agent channel.
        if (!AllowedActions.Contains(action) ||
            !serviceOptions.UnitNames.Contains(unit, StringComparer.Ordinal))
        {
            await AuditAsync("service.mutation_rejected", sessionIdHash, sourceIp,
                $"action={action}; reason=unit_not_allowed", cancellationToken);
            return new ServiceActionRequestResult(ServiceActionRequestOutcome.UnitNotAllowed);
        }

        if (ConfirmationRequiredActions.Contains(action))
        {
            return await CreateConfirmationAsync(unit, action, sessionIdHash, sourceIp, cancellationToken);
        }

        // start is non-destructive (D20): direct execution.
        var execution = await ExecuteAsync(unit, action, sessionIdHash, sourceIp, cancellationToken);
        return new ServiceActionRequestResult(ServiceActionRequestOutcome.Executed, Execution: execution);
    }

    /// <summary>
    /// Confirms a pending destructive action (plan §3.4): the token must exist,
    /// be unconsumed, unexpired, and belong to the current session (TA9).
    /// </summary>
    public async Task<ServiceActionRequestResult> ConfirmAsync(
        string rawToken,
        string sessionIdHash,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        var idHash = HashToken(rawToken);
        var confirmation = await confirmationStore.GetByIdHashAsync(idHash, cancellationToken);

        // TA9: unknown, consumed, expired, or session-mismatched tokens are all
        // rejected with the same generic outcome (no information leak), and the
        // rejection is audited.
        if (confirmation is null ||
            confirmation.Consumed ||
            confirmation.ExpiresUtc <= timeProvider.GetUtcNow().UtcDateTime ||
            !string.Equals(confirmation.SessionHash, sessionIdHash, StringComparison.Ordinal))
        {
            await AuditAsync("service.confirm_rejected", sessionIdHash, sourceIp,
                "reason=invalid_or_expired", cancellationToken);
            return new ServiceActionRequestResult(ServiceActionRequestOutcome.InvalidConfirmation);
        }

        // Single-use: consume before execution so a concurrent duplicate confirm
        // cannot double-execute (plan §3.4: consumed on confirm AND on failure).
        if (!await confirmationStore.TryConsumeAsync(idHash, cancellationToken))
        {
            await AuditAsync("service.confirm_rejected", sessionIdHash, sourceIp,
                "reason=already_consumed", cancellationToken);
            return new ServiceActionRequestResult(ServiceActionRequestOutcome.InvalidConfirmation);
        }

        var execution = await ExecuteAsync(
            confirmation.Unit, confirmation.Action, sessionIdHash, sourceIp, cancellationToken);
        return new ServiceActionRequestResult(ServiceActionRequestOutcome.Executed, Execution: execution);
    }

    /// <summary>Discards a pending confirmation (plan §3.4 cancel path).</summary>
    public async Task<ServiceActionRequestResult> CancelAsync(
        string rawToken,
        string sessionIdHash,
        string? sourceIp,
        CancellationToken cancellationToken = default)
    {
        var idHash = HashToken(rawToken);
        var confirmation = await confirmationStore.GetByIdHashAsync(idHash, cancellationToken);

        if (confirmation is null ||
            !string.Equals(confirmation.SessionHash, sessionIdHash, StringComparison.Ordinal))
        {
            await AuditAsync("service.cancel_rejected", sessionIdHash, sourceIp,
                "reason=invalid", cancellationToken);
            return new ServiceActionRequestResult(ServiceActionRequestOutcome.InvalidConfirmation);
        }

        await confirmationStore.DeleteAsync(idHash, cancellationToken);
        await AuditAsync("service.cancel", sessionIdHash, sourceIp,
            $"unit={confirmation.Unit}; action={confirmation.Action}", cancellationToken);
        return new ServiceActionRequestResult(ServiceActionRequestOutcome.Cancelled);
    }

    private async Task<ServiceActionRequestResult> CreateConfirmationAsync(
        string unit, string action, string sessionIdHash, string? sourceIp, CancellationToken cancellationToken)
    {
        // 256-bit random token (≥ 128-bit requirement, plan §3.4); only its
        // SHA-256 hash is persisted (D25).
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenByteCount));
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var confirmation = new ServiceActionConfirmation(
            IdHash: HashToken(rawToken),
            Unit: unit,
            Action: action,
            SessionHash: sessionIdHash,
            CreatedUtc: nowUtc,
            ExpiresUtc: nowUtc + DefaultConfirmationTtl,
            Consumed: false);

        await confirmationStore.InsertAsync(confirmation, cancellationToken);

        await AuditAsync("service.confirmation_issued", sessionIdHash, sourceIp,
            $"unit={unit}; action={action}", cancellationToken);

        return new ServiceActionRequestResult(
            ServiceActionRequestOutcome.ConfirmationRequired,
            Pending: new ServiceActionPending(rawToken, unit, action, confirmation.ExpiresUtc));
    }

    private async Task<ServiceActionExecution> ExecuteAsync(
        string unit, string action, string sessionIdHash, string? sourceIp, CancellationToken cancellationToken)
    {
        // Failure grouping (plan task 6, T16): repeated identical agent failures
        // for the same unit+action record only the transition into failure; the
        // next success row doubles as the recovery evidence (TA11).
        var failureKey = $"service.mutation|{unit}|{action}";

        if (!agentChannel.IsAvailable)
        {
            // D24: structured unavailability; audited as Failure (TA10), grouped
            // when repeated (task 6): a burst of retries writes one Failure row.
            await failureGrouper.RecordFailureAsync(
                failureKey, "service.mutation",
                $"unit={unit}; action={action}; reason=agent_unavailable",
                sourceIp, cancellationToken, actor: "admin");
            return new ServiceActionExecution(
                unit, action, AgentOperationOutcome.Rejected, "Agent unavailable",
                timeProvider.GetUtcNow().UtcDateTime);
        }

        var result = await agentChannel.SendServiceActionAsync(unit, action, cancellationToken);
        var succeeded = result.Outcome == AgentOperationOutcome.Success;

        if (succeeded)
        {
            // Always write the success row; it also serves as the recovery
            // record, so clear the failure state without a duplicate row.
            failureGrouper.EndFailure(failureKey);
            await AuditAsync("service.mutation", sessionIdHash, sourceIp,
                $"unit={unit}; action={action}; outcome={result.Outcome}", cancellationToken,
                AuditOutcomes.Success);
        }
        else
        {
            await failureGrouper.RecordFailureAsync(
                failureKey, "service.mutation",
                $"unit={unit}; action={action}; outcome={result.Outcome}",
                sourceIp, cancellationToken, actor: "admin");
        }

        return new ServiceActionExecution(
            unit, action, result.Outcome, result.Detail, timeProvider.GetUtcNow().UtcDateTime);
    }

    private Task AuditAsync(
        string action, string sessionIdHash, string? sourceIp, string detail,
        CancellationToken cancellationToken, string outcome = AuditOutcomes.Success)
    {
        // Detail carries unit/action/reason only — never tokens or session ids (D25).
        return auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "admin",
            Action = action,
            Outcome = outcome,
            SourceIp = sourceIp,
            Detail = detail,
        }, cancellationToken);
    }

    /// <summary>SHA-256 of the raw token — the only persisted form (D25).</summary>
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private const int TokenByteCount = 32; // 256 bits ≥ 128-bit requirement (plan §3.4)
}
