using HostingAdmin.Core.Services;
using HostingAdmin.Infrastructure.Persistence;
using Xunit;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Confirmation store acceptance checks (plan task 4, §3.5): insert/lookup,
/// atomic single-use consumption, delete, and expiry pruning. Hash-only
/// storage is enforced by the service contract (D25) and tested in Core.
/// </summary>
public class SqliteServiceActionConfirmationStoreTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteServiceActionConfirmationStore _store;

    public SqliteServiceActionConfirmationStoreTests()
    {
        _tempDbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hostingadmin-conf-test-{Guid.NewGuid():N}.db");

        _factory = new SqliteConnectionFactory(
            new DatabaseOptions { Path = _tempDbPath },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteConnectionFactory>.Instance);

        _store = new SqliteServiceActionConfirmationStore(
            _factory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteServiceActionConfirmationStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            if (File.Exists(_tempDbPath + "-wal")) File.Delete(_tempDbPath + "-wal");
            if (File.Exists(_tempDbPath + "-shm")) File.Delete(_tempDbPath + "-shm");
        }
        catch (IOException)
        {
            // Best-effort cleanup on Windows file locking.
        }
    }

    [Fact]
    public async Task Insert_then_get_round_trips_all_fields()
    {
        var confirmation = MakeConfirmation("hash-1");

        await _store.InsertAsync(confirmation);
        var loaded = await _store.GetByIdHashAsync("hash-1");

        Assert.NotNull(loaded);
        Assert.Equal(confirmation.IdHash, loaded!.IdHash);
        Assert.Equal(confirmation.Unit, loaded.Unit);
        Assert.Equal(confirmation.Action, loaded.Action);
        Assert.Equal(confirmation.SessionHash, loaded.SessionHash);
        Assert.Equal(confirmation.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(confirmation.ExpiresUtc, loaded.ExpiresUtc);
        Assert.False(loaded.Consumed);
    }

    [Fact]
    public async Task Get_unknown_hash_returns_null()
    {
        Assert.Null(await _store.GetByIdHashAsync("nope"));
    }

    [Fact]
    public async Task TryConsume_succeeds_once_then_fails()
    {
        await _store.InsertAsync(MakeConfirmation("hash-1"));

        Assert.True(await _store.TryConsumeAsync("hash-1"));
        Assert.False(await _store.TryConsumeAsync("hash-1"));

        var row = await _store.GetByIdHashAsync("hash-1");
        Assert.True(row!.Consumed);
    }

    [Fact]
    public async Task TryConsume_unknown_hash_returns_false()
    {
        Assert.False(await _store.TryConsumeAsync("missing"));
    }

    [Fact]
    public async Task Delete_removes_row_and_is_idempotent()
    {
        await _store.InsertAsync(MakeConfirmation("hash-1"));

        await _store.DeleteAsync("hash-1");
        await _store.DeleteAsync("hash-1"); // idempotent

        Assert.Null(await _store.GetByIdHashAsync("hash-1"));
    }

    [Fact]
    public async Task PruneExpired_removes_only_expired_rows()
    {
        var now = DateTime.Parse("2026-10-23T12:00:00.0000000Z", styles: System.Globalization.DateTimeStyles.RoundtripKind);
        await _store.InsertAsync(MakeConfirmation("expired", expiresUtc: now.AddMinutes(-1)));
        await _store.InsertAsync(MakeConfirmation("live", expiresUtc: now.AddMinutes(5)));

        var removed = await _store.PruneExpiredAsync(now);

        Assert.Equal(1, removed);
        Assert.Null(await _store.GetByIdHashAsync("expired"));
        Assert.NotNull(await _store.GetByIdHashAsync("live"));
    }

    private static ServiceActionConfirmation MakeConfirmation(
        string idHash,
        string unit = "nginx.service",
        string action = "stop",
        string sessionHash = "session-hash-a",
        DateTime? expiresUtc = null) => new(
            IdHash: idHash,
            Unit: unit,
            Action: action,
            SessionHash: sessionHash,
            CreatedUtc: new DateTime(2026, 10, 23, 11, 55, 0, DateTimeKind.Utc),
            ExpiresUtc: expiresUtc ?? new DateTime(2026, 10, 23, 12, 5, 0, DateTimeKind.Utc),
            Consumed: false);
}
