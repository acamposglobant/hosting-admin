using HostingAdmin.Core.Sessions;
using HostingAdmin.Infrastructure.Persistence;
using Xunit;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Session store acceptance checks (plan task 5, T8–T11): expiration and
/// revocation behavior via the store, cleanup pruning, and hash-only storage.
/// </summary>
public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _tempDbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hostingadmin-sess-test-{Guid.NewGuid():N}.db");

        _factory = new SqliteConnectionFactory(
            new DatabaseOptions { Path = _tempDbPath },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteConnectionFactory>.Instance);

        _store = new SqliteSessionStore(_factory);
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

    private static SessionRecord MakeRecord(
        string idHash = "hash-1",
        DateTime? expiresUtc = null,
        DateTime? idleExpiresUtc = null,
        bool revoked = false) => new()
    {
        IdHash = idHash,
        AdminId = "admin",
        CreatedUtc = DateTime.UtcNow,
        ExpiresUtc = expiresUtc ?? DateTime.UtcNow.AddHours(12),
        IdleExpiresUtc = idleExpiresUtc,
        Revoked = revoked,
    };

    [Fact]
    public async Task InsertAndGet_RoundTripsRecord()
    {
        var record = MakeRecord(idHash: SessionService.HashId("raw-1"));
        await _store.InsertAsync(record);

        var loaded = await _store.GetByIdHashAsync(SessionService.HashId("raw-1"));

        Assert.NotNull(loaded);
        Assert.Equal(record.IdHash, loaded.IdHash);
        Assert.Equal(record.AdminId, loaded.AdminId);
        Assert.Equal(record.ExpiresUtc, loaded.ExpiresUtc);
        Assert.Null(loaded.IdleExpiresUtc);
        Assert.False(loaded.Revoked);
    }

    [Fact]
    public async Task GetByIdHashAsync_UnknownHash_ReturnsNull()
    {
        Assert.Null(await _store.GetByIdHashAsync("does-not-exist"));
    }

    [Fact]
    public async Task Revoke_MarksSessionRevoked()
    {
        var idHash = SessionService.HashId("raw-revoke");
        await _store.InsertAsync(MakeRecord(idHash: idHash));

        await _store.RevokeAsync(idHash);

        var record = await _store.GetByIdHashAsync(idHash);
        Assert.NotNull(record);
        Assert.True(record.Revoked);
    }

    [Fact]
    public async Task Revoke_IsIdempotent()
    {
        var idHash = SessionService.HashId("raw-idempotent");
        await _store.InsertAsync(MakeRecord(idHash: idHash));

        await _store.RevokeAsync(idHash);
        await _store.RevokeAsync(idHash);

        var record = await _store.GetByIdHashAsync(idHash);
        Assert.NotNull(record);
        Assert.True(record.Revoked);
    }

    [Fact]
    public async Task PruneExpired_RemovesExpired_KeepsActive()
    {
        // T8/T10: past-absolute-expiration rows are pruned; active rows survive.
        var now = DateTime.UtcNow;
        await _store.InsertAsync(MakeRecord(idHash: "expired", expiresUtc: now.AddMinutes(-1)));
        await _store.InsertAsync(MakeRecord(idHash: "active", expiresUtc: now.AddHours(1)));

        var deleted = await _store.PruneExpiredAsync(now);

        Assert.Equal(1, deleted);
        Assert.Null(await _store.GetByIdHashAsync("expired"));
        Assert.NotNull(await _store.GetByIdHashAsync("active"));
    }

    [Fact]
    public async Task PruneExpired_RemovesRevoked()
    {
        // T9/T10: revoked rows are pruned.
        var now = DateTime.UtcNow;
        await _store.InsertAsync(MakeRecord(idHash: "revoked-row", revoked: true));

        var deleted = await _store.PruneExpiredAsync(now);

        Assert.Equal(1, deleted);
        Assert.Null(await _store.GetByIdHashAsync("revoked-row"));
    }

    [Fact]
    public async Task PruneExpired_RemovesIdleExpired_WhenIdleSet()
    {
        var now = DateTime.UtcNow;
        await _store.InsertAsync(MakeRecord(idHash: "idle-expired", idleExpiresUtc: now.AddMinutes(-1)));
        await _store.InsertAsync(MakeRecord(idHash: "idle-active", idleExpiresUtc: now.AddMinutes(30)));

        var deleted = await _store.PruneExpiredAsync(now);

        Assert.Equal(1, deleted);
        Assert.Null(await _store.GetByIdHashAsync("idle-expired"));
        Assert.NotNull(await _store.GetByIdHashAsync("idle-active"));
    }

    [Fact]
    public async Task RawSessionId_IsNeverStoredInDatabase()
    {
        // T11: only the hash is stored; the raw id never appears in the database.
        var rawId = "raw-secret-id-abc123";
        var service = new SessionService(new SessionOptions());
        var (_, record) = service.Create("admin", DateTime.UtcNow);

        await _store.InsertAsync(record);

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE id_hash LIKE $raw;";
        command.Parameters.AddWithValue("$raw", $"%{rawId}%");
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, count);
    }
}
