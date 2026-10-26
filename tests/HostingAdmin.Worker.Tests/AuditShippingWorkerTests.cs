using HostingAdmin.Core.Audit;
using HostingAdmin.Infrastructure.Persistence;
using HostingAdmin.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HostingAdmin.Worker.Tests;

/// <summary>
/// TA15/TA16/TA19: worker behavior against a real temp SQLite database and a
/// recording (in-memory) syslog sink. Portable — runs on Windows and Linux CI.
/// </summary>
public class AuditShippingWorkerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteAuditStore _store;
    private readonly SqliteAuditShippingCursorStore _cursorStore;
    private readonly RecordingSyslogWriter _writer = new();

    public AuditShippingWorkerTests()
    {
        _tempDbPath = Path.Combine(
            Path.GetTempPath(),
            $"hostingadmin-ship-test-{Guid.NewGuid():N}.db");

        var factory = new SqliteConnectionFactory(
            new DatabaseOptions { Path = _tempDbPath },
            NullLogger<SqliteConnectionFactory>.Instance);

        _store = new SqliteAuditStore(factory, NullLogger<SqliteAuditStore>.Instance);
        _cursorStore = new SqliteAuditShippingCursorStore(factory);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            foreach (var sidecar in new[] { _tempDbPath + "-wal", _tempDbPath + "-shm" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private async Task<AuditShippingWorker> AppendEventsAsync(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            await _store.AppendAsync(new AuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Actor = "admin",
                Action = $"test.event.{index}",
                Outcome = AuditOutcomes.Success,
                SourceIp = "127.0.0.1",
                Detail = $"event {index}",
            });
        }

        return CreateWorker(enabled: true);
    }

    private AuditShippingWorker CreateWorker(bool enabled, ISyslogWriter? writer = null) =>
        new(
            _store,
            _cursorStore,
            writer ?? _writer,
            new AuditShippingOptions { Enabled = enabled, PollIntervalSeconds = 1, BatchSize = 2 },
            NullLogger<AuditShippingWorker>.Instance);

    // --- TA15: one line per event, id order, resumable cursor ---

    [Fact]
    public async Task Ship_WritesOneLinePerEvent_InIdOrder()
    {
        await AppendEventsAsync(3);

        // One poll cycle: start, ship immediately, stop.
        await RunOnePollAsync(CreateWorker(enabled: true), expectedCursorId: 3);

        Assert.Equal(3, _writer.Lines.Count);
        Assert.All(_writer.Lines, line => Assert.DoesNotContain('\n', line));
        Assert.Contains("\"id\":1", _writer.Lines[0]);
        Assert.Contains("\"id\":2", _writer.Lines[1]);
        Assert.Contains("\"id\":3", _writer.Lines[2]);
    }

    [Fact]
    public async Task Ship_RestartResumesFromCursor_NoDuplicateNoSkip()
    {
        await AppendEventsAsync(5);

        // First run: ship everything (batch size 2 → 3 drain cycles), then
        // "restart" with a fresh worker.
        await RunOnePollAsync(CreateWorker(enabled: true), expectedCursorId: 5);
        Assert.Equal(5, _writer.Lines.Count);

        _writer.Lines.Clear();

        // Append one more event, then a new worker instance (simulated restart)
        // must ship only the new event — no duplication of ids 1-5, no skip.
        await _store.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "admin",
            Action = "test.event.6",
            Outcome = AuditOutcomes.Success,
            SourceIp = "127.0.0.1",
            Detail = "event 6",
        });

        await RunOnePollAsync(CreateWorker(enabled: true), expectedCursorId: 6);

        var shippedId = Assert.Single(_writer.Lines);
        Assert.Contains("\"id\":6", shippedId);
    }

    // --- TA16: sink failure is non-fatal, cursor not advanced, rows intact ---

    [Fact]
    public async Task Ship_SinkFailure_DoesNotAdvanceCursorOrMutateRows()
    {
        var failingWriter = new FailingSyslogWriter();
        await AppendEventsAsync(2);

        await RunOnePollAsync(CreateWorker(enabled: true, writer: failingWriter), expectedCursorId: 0);

        // Nothing shipped, cursor untouched.
        Assert.Empty(failingWriter.Lines);
        Assert.Equal(0, await _cursorStore.GetLastShippedIdAsync());

        // Audit rows are untouched (worker never mutates them).
        var page = await _store.QueryAsync(0, 200);
        Assert.Equal(2, page.TotalCount);

        // After the sink recovers, everything ships exactly once.
        await RunOnePollAsync(CreateWorker(enabled: true, writer: _writer), expectedCursorId: 2);
        Assert.Equal(2, _writer.Lines.Count);
    }

    // --- TA19: disabled by default, zero writes ---

    [Fact]
    public async Task Disabled_ByDefault_ProducesZeroWrites()
    {
        await AppendEventsAsync(3);

        var worker = CreateWorker(enabled: false);
        await RunWorkerUntilAsync(worker, () => worker.ExecuteTask is { IsCompleted: true });

        Assert.Empty(_writer.Lines);
        Assert.Equal(0, await _cursorStore.GetLastShippedIdAsync());
    }

    [Fact]
    public void Options_DefaultIsDisabled()
    {
        var options = new AuditShippingOptions();
        Assert.False(options.Enabled);
    }

    // --- helpers ---

    // --- helpers ---

    /// <summary>
    /// Starts the worker and waits until the given condition holds (the worker
    /// ships immediately on startup, then polls), with a generous timeout so a
    /// broken worker fails the test instead of hanging it.
    /// </summary>
    private static async Task RunWorkerUntilAsync(AuditShippingWorker worker, Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execute = worker.StartAsync(cts.Token);

        try
        {
            while (!condition() && !cts.IsCancellationRequested)
            {
                await Task.Delay(25);
            }

            Assert.True(condition(), "worker did not reach the expected state within the timeout");
        }
        finally
        {
            cts.Cancel();
            await execute; // rethrows worker startup failures
        }
    }

    /// <summary>Runs one shipping cycle and waits for the cursor to reach the expected id.</summary>
    private Task RunOnePollAsync(AuditShippingWorker worker, long expectedCursorId) =>
        RunWorkerUntilAsync(worker, () =>
        {
            var value = _cursorStore.GetLastShippedIdAsync().GetAwaiter().GetResult();
            return value == expectedCursorId;
        });

    private sealed class RecordingSyslogWriter : ISyslogWriter
    {
        public List<string> Lines { get; } = [];

        public Task WriteAsync(string line, CancellationToken cancellationToken = default)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSyslogWriter : ISyslogWriter
    {
        public List<string> Lines { get; } = [];

        public Task WriteAsync(string line, CancellationToken cancellationToken = default) =>
            throw new IOException("syslog sink unavailable (simulated)");
    }
}
