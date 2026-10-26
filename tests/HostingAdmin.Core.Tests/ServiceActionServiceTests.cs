using HostingAdmin.Core.Agent;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Services;
using HostingAdmin.Core.Sessions;
using Xunit;

namespace HostingAdmin.Core.Tests;

/// <summary>
/// Service mutation use-case tests (plan task 4, TA7–TA9): API-side allowlist
/// enforcement (D18), direct vs. confirmed execution (D20), single-use +
/// session-bound + TTL confirmation semantics, and audit for every step (D21).
/// </summary>
public class ServiceActionServiceTests
{
    private static readonly string[] AllowlistedUnits = ["nginx.service", "ssh.service"];

    private readonly RecordingAuditStore _auditStore = new();
    private readonly FakeAgentChannel _channel = new();
    private readonly InMemoryConfirmationStore _confirmations = new();
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ServiceActionService CreateService() => new(
        new ServiceStatusOptions { UnitNames = AllowlistedUnits },
        _channel,
        _confirmations,
        _auditStore,
        new FailureAuditGrouper(_auditStore),
        _time);

    // --- TA8: non-allowlisted unit never reaches the channel ---

    [Fact]
    public async Task NonAllowlistedUnit_is_rejected_and_channel_never_called()
    {
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "evil.service", "start", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.UnitNotAllowed, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
        Assert.Contains(_auditStore.Events, e => e.Action == "service.mutation_rejected");
    }

    [Fact]
    public async Task UnknownAction_is_rejected_and_channel_never_called()
    {
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "nginx.service", "reload", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.UnitNotAllowed, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
    }

    // --- start executes directly (D20) ---

    [Fact]
    public async Task Start_executes_directly_without_confirmation()
    {
        _channel.NextOutcome = AgentOperationOutcome.Success;
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "nginx.service", "start", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.Executed, result.Outcome);
        Assert.Equal(AgentOperationOutcome.Success, result.Execution!.Outcome);
        Assert.Equal(0, _confirmations.Count);
    }

    // --- stop/restart create confirmations (D20) ---

    [Fact]
    public async Task Stop_creates_confirmation_and_does_not_execute()
    {
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "nginx.service", "stop", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal(0, _channel.CallCount); // no execution yet
        Assert.Equal(1, _confirmations.Count);
        Assert.NotNull(result.Pending);
        Assert.Equal("nginx.service", result.Pending!.Unit);
        Assert.Equal("stop", result.Pending.Action);
        Assert.Equal(_time.UtcNow + TimeSpan.FromMinutes(5), result.Pending.ExpiresUtc);
        Assert.Contains(_auditStore.Events, e => e.Action == "service.confirmation_issued");
    }

    [Fact]
    public async Task Restart_creates_confirmation()
    {
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "nginx.service", "restart", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
    }

    // --- TA9: confirm validation ---

    [Fact]
    public async Task Confirm_with_valid_token_executes_and_consumes()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");

        var result = await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.Executed, result.Outcome);
        Assert.Equal(1, _channel.CallCount);
        Assert.Equal(1, _confirmations.Count);
        Assert.True(_confirmations.Rows[0].Consumed);
    }

    [Fact]
    public async Task Confirm_with_unknown_token_is_rejected()
    {
        var service = CreateService();

        var result = await service.ConfirmAsync("bogus-token", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
    }

    [Fact]
    public async Task Confirm_twice_second_fails_single_use()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");
        await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        var second = await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, second.Outcome);
        Assert.Equal(1, _channel.CallCount); // executed exactly once
    }

    [Fact]
    public async Task Confirm_from_different_session_is_rejected()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");

        var result = await service.ConfirmAsync(pending.ConfirmationId, "session-b", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
    }

    [Fact]
    public async Task Confirm_after_expiry_is_rejected()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");
        _time.Advance(TimeSpan.FromMinutes(6)); // TTL is 5 minutes

        var result = await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, result.Outcome);
        Assert.Equal(0, _channel.CallCount);
    }

    [Fact]
    public async Task Confirm_within_ttl_succeeds()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");
        _time.Advance(TimeSpan.FromMinutes(4));

        var result = await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.Executed, result.Outcome);
    }

    // --- cancel ---

    [Fact]
    public async Task Cancel_discards_confirmation()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");

        var result = await service.CancelAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, _confirmations.Count);
        Assert.Contains(_auditStore.Events, e => e.Action == "service.cancel");

        // Confirm after cancel is rejected.
        var confirm = await service.ConfirmAsync(pending.ConfirmationId, "session-a", null, CancellationToken.None);
        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, confirm.Outcome);
    }

    [Fact]
    public async Task Cancel_from_different_session_is_rejected()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");

        var result = await service.CancelAsync(pending.ConfirmationId, "session-b", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.InvalidConfirmation, result.Outcome);
        Assert.Equal(1, _confirmations.Count); // row untouched
    }

    // --- TA10: agent unavailable ---

    [Fact]
    public async Task Agent_unavailable_reports_capability_unavailable_and_audits_failure()
    {
        _channel.IsAvailable = false;
        var service = CreateService();

        var result = await service.RequestActionAsync(
            "nginx.service", "start", "session-a", null, CancellationToken.None);

        Assert.Equal(ServiceActionRequestOutcome.Executed, result.Outcome);
        Assert.Equal(AgentOperationOutcome.Rejected, result.Execution!.Outcome);
        Assert.Contains(_auditStore.Events, e => e.Outcome == AuditOutcomes.Failure);
    }

    // --- task 6: failure grouping for repeated agent failures (T16, TA11) ---

    [Fact]
    public async Task Repeated_agent_unavailable_burst_writes_exactly_one_failure_row()
    {
        _channel.IsAvailable = false;
        var service = CreateService();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await service.RequestActionAsync(
                "nginx.service", "start", "session-a", "203.0.113.7", CancellationToken.None);
        }

        var failureRows = _auditStore.Events
            .Where(e => e.Action == "service.mutation" && e.Outcome == AuditOutcomes.Failure)
            .ToList();
        Assert.Single(failureRows);
        Assert.Equal("admin", failureRows[0].Actor);
        Assert.Equal("203.0.113.7", failureRows[0].SourceIp);
        Assert.Contains("agent_unavailable", failureRows[0].Detail);
    }

    [Fact]
    public async Task Repeated_agent_rejections_for_same_unit_action_are_grouped()
    {
        _channel.NextOutcome = AgentOperationOutcome.Rejected;
        var service = CreateService();

        // start executes directly, so repeated rejections hit the same key.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await service.RequestActionAsync(
                "nginx.service", "start", "session-a", null, CancellationToken.None);
        }

        var failureRows = _auditStore.Events
            .Where(e => e.Action == "service.mutation" && e.Outcome == AuditOutcomes.Failure)
            .ToList();
        Assert.Single(failureRows);
    }

    [Fact]
    public async Task Failure_grouping_is_per_unit_and_action()
    {
        _channel.IsAvailable = false;
        var service = CreateService();

        // Two distinct direct keys: nginx|start and ssh|start.
        await service.RequestActionAsync("nginx.service", "start", "session-a", null, CancellationToken.None);
        await service.RequestActionAsync("ssh.service", "start", "session-a", null, CancellationToken.None);

        // A third distinct key via the confirm path: nginx|stop.
        var pending = await RequestStopAsync(service, "session-a");
        await service.ConfirmAsync(
            pending.ConfirmationId, "session-a", null, CancellationToken.None);

        var failureRows = _auditStore.Events
            .Where(e => e.Action == "service.mutation" && e.Outcome == AuditOutcomes.Failure)
            .ToList();
        Assert.Equal(3, failureRows.Count);
    }

    [Fact]
    public async Task Success_after_failures_writes_recovery_row_and_rearms_grouping()
    {
        _channel.IsAvailable = false;
        var service = CreateService();

        // Two failures: only the first writes a Failure row.
        await service.RequestActionAsync("nginx.service", "start", "session-a", null, CancellationToken.None);
        await service.RequestActionAsync("nginx.service", "start", "session-a", null, CancellationToken.None);
        Assert.Single(_auditStore.Events.Where(e => e.Outcome == AuditOutcomes.Failure));

        // Recovery: the success row is written and clears the failure state.
        _channel.IsAvailable = true;
        _channel.NextOutcome = AgentOperationOutcome.Success;
        await service.RequestActionAsync("nginx.service", "start", "session-a", null, CancellationToken.None);

        var successRows = _auditStore.Events
            .Where(e => e.Action == "service.mutation" && e.Outcome == AuditOutcomes.Success)
            .ToList();
        Assert.Single(successRows);
        Assert.Equal(2, _auditStore.Events.Count); // 1 failure + 1 success, no duplicates

        // Re-armed: a new failure after recovery writes a fresh Failure row.
        _channel.IsAvailable = false;
        await service.RequestActionAsync("nginx.service", "start", "session-a", null, CancellationToken.None);
        Assert.Equal(2, _auditStore.Events.Count(e => e.Outcome == AuditOutcomes.Failure));
    }

    // --- D25: token never persisted in raw form ---

    [Fact]
    public async Task Confirmation_token_is_stored_hashed_only()
    {
        var service = CreateService();
        var pending = await RequestStopAsync(service, "session-a");

        Assert.NotEqual(pending.ConfirmationId, _confirmations.Rows[0].IdHash);
        Assert.Equal(ServiceActionService.HashToken(pending.ConfirmationId), _confirmations.Rows[0].IdHash);
    }

    // --- helpers ---

    private async Task<ServiceActionPending> RequestStopAsync(ServiceActionService service, string sessionIdHash)
    {
        var result = await service.RequestActionAsync(
            "nginx.service", "stop", sessionIdHash, null, CancellationToken.None);
        Assert.Equal(ServiceActionRequestOutcome.ConfirmationRequired, result.Outcome);
        return result.Pending!;
    }

    private sealed class FakeTimeProvider(DateTime startUtc) : TimeProvider
    {
        private DateTime _utcNow = startUtc;

        public DateTime UtcNow => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => new(_utcNow, TimeSpan.Zero);
    }

    private sealed class FakeAgentChannel : IAgentChannel
    {
        public bool IsAvailable { get; set; } = true;

        public int CallCount { get; private set; }

        public AgentOperationOutcome NextOutcome { get; set; } = AgentOperationOutcome.Success;

        public Task<AgentChannelResult> SendServiceActionAsync(
            string unit, string action, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new AgentChannelResult(NextOutcome, "shim"));
        }

        public Task<AgentChannelResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentChannelResult(AgentOperationOutcome.Success, "pong"));
    }

    private sealed class InMemoryConfirmationStore : IServiceActionConfirmationStore
    {
        public List<ServiceActionConfirmation> Rows { get; } = [];

        public int Count => Rows.Count;

        public Task InsertAsync(ServiceActionConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(c => c.IdHash == confirmation.IdHash);
            Rows.Add(confirmation);
            return Task.CompletedTask;
        }

        public Task<ServiceActionConfirmation?> GetByIdHashAsync(
            string idHash, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Rows.FirstOrDefault(c => c.IdHash == idHash));
        }

        public Task<bool> TryConsumeAsync(string idHash, CancellationToken cancellationToken = default)
        {
            var index = Rows.FindIndex(c => c.IdHash == idHash && !c.Consumed);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Rows[index] = Rows[index] with { Consumed = true };
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string idHash, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(c => c.IdHash == idHash);
            return Task.CompletedTask;
        }

        public Task<int> PruneExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var before = Rows.Count;
            Rows.RemoveAll(c => c.ExpiresUtc <= nowUtc);
            return Task.FromResult(before - Rows.Count);
        }
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<AuditPage> QueryAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AuditEventRow>> QueryAfterIdAsync(long afterId, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
