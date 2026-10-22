using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Logging;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Task 4 acceptance checks: temp-DB insert/read-back; prune honors the
/// configured window (default 90 d); schema is idempotent.
/// </summary>
public class SqliteAuditStoreTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteAuditStore _store;

    public SqliteAuditStoreTests()
    {
        _tempDbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hostingadmin-test-{Guid.NewGuid():N}.db");

        _factory = new SqliteConnectionFactory(
            new DatabaseOptions { Path = _tempDbPath },
            NullLogger<SqliteConnectionFactory>.Instance);

        _store = new SqliteAuditStore(
            _factory,
            NullLogger<SqliteAuditStore>.Instance);
    }

    public void Dispose()
    {
        // WAL sidecar files (-wal, -shm) are removed with the main file.
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            var wal = _tempDbPath + "-wal";
            var shm = _tempDbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static AuditEvent MakeEvent(string action = "auth.login", string actor = "admin",
        string outcome = AuditOutcomes.Success, string? detail = null, DateTime? timestamp = null) => new()
    {
        TimestampUtc = timestamp ?? DateTime.UtcNow,
        Actor = actor,
        Action = action,
        Outcome = outcome,
        SourceIp = "127.0.0.1",
        Detail = detail,
    };

    private async Task<long> CountByActionAsync(string action)
    {
        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = $action;";
        command.Parameters.AddWithValue("$action", action);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AppendAsync_ThenReadBack_RoundTripsAllFields()
    {
        var timestamp = DateTime.UtcNow;
        await _store.AppendAsync(MakeEvent(detail: "login ok", timestamp: timestamp));

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT timestamp_utc, actor, action, outcome, source_ip, detail FROM audit_events;";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("admin", reader.GetString(1));
        Assert.Equal("auth.login", reader.GetString(2));
        Assert.Equal("Success", reader.GetString(3));
        Assert.Equal("127.0.0.1", reader.GetString(4));
        Assert.Equal("login ok", reader.GetString(5));

        var persistedTimestamp = DateTime.Parse(reader.GetString(0)).ToUniversalTime();
        Assert.Equal(timestamp, persistedTimestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AppendAsync_ScrubsSecretMaterialInDetail()
    {
        await _store.AppendAsync(MakeEvent(detail: """{"password":"hunter2","status":"ok"}"""));

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM audit_events;";
        var detail = (string?)(await command.ExecuteScalarAsync());

        Assert.NotNull(detail);
        Assert.DoesNotContain("hunter2", detail);
        Assert.Contains(SecretRedactor.RedactedPlaceholder, detail);
    }

    [Fact]
    public async Task PruneOlderThanAsync_DeletesOnlyEventsOlderThanCutoff()
    {
        await _store.AppendAsync(MakeEvent(action: "old.event", timestamp: DateTime.UtcNow.AddDays(-100)));
        await _store.AppendAsync(MakeEvent(action: "recent.event", timestamp: DateTime.UtcNow.AddDays(-1)));

        var deleted = await _store.PruneOlderThanAsync(DateTime.UtcNow.AddDays(-90));

        Assert.Equal(1, deleted);
        Assert.Equal(0, await CountByActionAsync("old.event"));
        Assert.Equal(1, await CountByActionAsync("recent.event"));
    }

    [Fact]
    public async Task PruneOlderThanAsync_WithDefault90DayWindow_KeepsRecentEvents()
    {
        // Simulates the configured default (90 d): an 89-day-old event survives,
        // a 91-day-old event is pruned.
        var now = DateTime.UtcNow;
        await _store.AppendAsync(MakeEvent(action: "edge", timestamp: now.AddDays(-89)));
        await _store.AppendAsync(MakeEvent(action: "ancient", timestamp: now.AddDays(-91)));

        var deleted = await _store.PruneOlderThanAsync(now.AddDays(-90));

        Assert.Equal(1, deleted);
        Assert.Equal(1, await CountByActionAsync("edge"));
        Assert.Equal(0, await CountByActionAsync("ancient"));
    }

    [Fact]
    public async Task SchemaInitializer_IsIdempotent_WhenRunTwice()
    {
        // First initialization happens implicitly via AppendAsync.
        await _store.AppendAsync(MakeEvent());

        // Second initialization must not throw and must not lose data.
        _factory.EnsureInitialized();

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events;";
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AppendAsync_ScrubsSecretMaterialFromDetail_BeforePersistence()
    {
        // Defense in depth (D7): even if a caller passes secret material in the
        // detail JSON, the store must not persist it in plaintext.
        var detail = """{"password":"hunter2","action":"login"}""";
        await _store.AppendAsync(MakeEvent(action: "redact.check", detail: detail));

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM audit_events WHERE action = $action;";
        command.Parameters.AddWithValue("$action", "redact.check");
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.DoesNotContain("hunter2", stored);
        Assert.Contains("***REDACTED***", stored);
    }

    [Fact]
    public async Task Schema_AllowsNullSourceIpAndDetail()
    {
        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (timestamp_utc, actor, action, outcome, source_ip, detail)
            VALUES ($timestamp_utc, $actor, $action, $outcome, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$timestamp_utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actor", "system");
        command.Parameters.AddWithValue("$action", "test.nullable");
        command.Parameters.AddWithValue("$outcome", "Success");
        await command.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT source_ip, detail FROM audit_events WHERE action = $action;";
        verify.Parameters.AddWithValue("$action", "test.nullable");
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    // --- Paged query (plan task 13) ---

    [Fact]
    public async Task QueryAsync_ReturnsNewestFirst_WithTotalCount()
    {
        var baseTime = DateTime.UtcNow;
        for (var index = 0; index < 5; index++)
        {
            await _store.AppendAsync(MakeEvent(action: $"event.{index}", timestamp: baseTime.AddSeconds(index)));
        }

        var page = await _store.QueryAsync(skip: 0, take: 10);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal("event.4", page.Items[0].Action); // newest first
        Assert.Equal("event.0", page.Items[4].Action);
    }

    [Fact]
    public async Task QueryAsync_Pagination_WalksAllRowsWithoutOverlap()
    {
        for (var index = 0; index < 7; index++)
        {
            await _store.AppendAsync(MakeEvent(action: $"event.{index}", timestamp: DateTime.UtcNow.AddSeconds(index)));
        }

        var page1 = await _store.QueryAsync(skip: 0, take: 3);
        var page2 = await _store.QueryAsync(skip: 3, take: 3);
        var page3 = await _store.QueryAsync(skip: 6, take: 3);

        Assert.Equal(7, page1.TotalCount);
        Assert.Equal(3, page1.Items.Count);
        Assert.Equal(3, page2.Items.Count);
        Assert.Single(page3.Items);

        var allActions = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(e => e.Action).ToList();
        Assert.Equal(7, allActions.Distinct().Count()); // no duplicates across pages
        Assert.Equal(7, allActions.Count());
    }

    [Fact]
    public async Task QueryAsync_EmptyStore_ReturnsEmptyPageWithZeroTotal()
    {
        var page = await _store.QueryAsync(skip: 0, take: 50);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task QueryAsync_SkipBeyondEnd_ReturnsEmptyItemsWithCorrectTotal()
    {
        await _store.AppendAsync(MakeEvent());

        var page = await _store.QueryAsync(skip: 10, take: 50);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }
}
