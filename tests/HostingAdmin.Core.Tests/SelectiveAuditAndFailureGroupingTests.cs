using HostingAdmin.Core.Audit;

namespace HostingAdmin.Core.Tests;

/// <summary>
/// Tests for selective audit-on-access (plan task 13, §3.7, T15) and failure
/// grouping (T16): first data access per session audited once; repeated
/// identical failures grouped into a state transition + recovery.
/// </summary>
public class SelectiveAuditAndFailureGroupingTests
{
    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recording.Add(auditEvent);
            return Task.CompletedTask;
        }

        public List<AuditEvent> Recording { get; } = [];

        public Task<AuditPage> QueryAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditPage([], Recording.Count));

        public Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<AuditEventRow>> QueryAfterIdAsync(long afterId, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditEventRow>>([]);
    }

    // --- Selective audit-on-access (first access per session, T15) ---

    [Fact]
    public async Task FirstDataAccess_IsAuditedOnce()
    {
        var store = new RecordingAuditStore();
        var service = new SelectiveAuditService(store);

        var first = await service.AuditDataAccessIfNeededAsync("hash-1", "/api/metrics", null);
        var second = await service.AuditDataAccessIfNeededAsync("hash-1", "/api/metrics", null);
        var third = await service.AuditDataAccessIfNeededAsync("hash-1", "/api/disks", null);

        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        var row = Assert.Single(store.Recording);
        Assert.Equal("data.access", row.Action);
        Assert.Equal(AuditOutcomes.Success, row.Outcome);
    }

    [Fact]
    public async Task DifferentSessions_EachGetOneFirstAccessRow()
    {
        var store = new RecordingAuditStore();
        var service = new SelectiveAuditService(store);

        await service.AuditDataAccessIfNeededAsync("hash-1", "/api/metrics", null);
        await service.AuditDataAccessIfNeededAsync("hash-1", "/api/metrics", null);
        await service.AuditDataAccessIfNeededAsync("hash-2", "/api/metrics", null);

        Assert.Equal(2, store.Recording.Count);
    }

    // --- Failure grouping (T16) ---

    [Fact]
    public async Task RepeatedIdenticalFailures_AreGroupedIntoOneRow()
    {
        var store = new RecordingAuditStore();
        var grouper = new FailureAuditGrouper(store);

        var first = await grouper.RecordFailureAsync("metrics:procfs", "provider.failure", "procfs unavailable", null);
        var second = await grouper.RecordFailureAsync("metrics:procfs", "metrics:procfs", "procfs unavailable", null);
        var third = await grouper.RecordFailureAsync("metrics:procfs", "metrics:procfs", "procfs unavailable", null);

        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        var row = Assert.Single(store.Recording);
        Assert.Equal(AuditOutcomes.Failure, row.Outcome);
    }

    [Fact]
    public async Task Recovery_IsAudited_AndNewFailureEpisodeRecordsAgain()
    {
        var store = new RecordingAuditStore();
        var grouper = new FailureAuditGrouper(store);

        await grouper.RecordFailureAsync("disks:statvfs", "disks.error", "statvfs failed", null);
        await grouper.RecordFailureAsync("disks:statvfs", "disks.statvfs", "statvfs failed", null); // grouped
        var recovered = await grouper.RecordRecoveryAsync("disks:statvfs", "disks.recovered", "statvfs ok", null);
        var refailed = await grouper.RecordFailureAsync("disks:statvfs", "disks.error", "statvfs failed", null);

        Assert.True(recovered);
        Assert.True(refailed);
        Assert.Equal(3, store.Recording.Count); // failure + recovery + new-episode failure
        Assert.Equal(AuditOutcomes.Failure, store.Recording[0].Outcome);
        Assert.Equal(AuditOutcomes.Success, store.Recording[1].Outcome);
        Assert.Equal(AuditOutcomes.Failure, store.Recording[2].Outcome);
    }

    [Fact]
    public async Task RecoveryWithoutFailureState_WritesNothing()
    {
        var store = new RecordingAuditStore();
        var grouper = new FailureAuditGrouper(store);

        var recovered = await grouper.RecordRecoveryAsync("never-failed", "x.recovered", null, null);

        Assert.False(recovered);
        Assert.Empty(store.Recording);
    }

    [Fact]
    public async Task DifferentFailureKeys_AreTrackedIndependently()
    {
        var store = new RecordingAuditStore();
        var grouper = new FailureAuditGrouper(store);

        await grouper.RecordFailureAsync("metrics", "a.failed", null, null);
        await grouper.RecordFailureAsync("disks", "b.failed", null, null);

        Assert.Equal(2, store.Recording.Count);
    }
}
