using HostingAdmin.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed session store (plan §3.5). All statements are parameterized;
/// only the session-id hash is ever persisted (T11).
/// </summary>
public sealed class SqliteSessionStore(SqliteConnectionFactory connectionFactory) : ISessionStore
{
    public async Task InsertAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id_hash, admin_id, created_utc, expires_utc, idle_expires_utc, revoked)
            VALUES ($id_hash, $admin_id, $created_utc, $expires_utc, $idle_expires_utc, $revoked);
            """;

        command.Parameters.AddWithValue("$id_hash", session.IdHash);
        command.Parameters.AddWithValue("$admin_id", session.AdminId);
        command.Parameters.AddWithValue("$created_utc", session.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$expires_utc", session.ExpiresUtc.ToString("O"));
        command.Parameters.AddWithValue("$idle_expires_utc", (object?)session.IdleExpiresUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$revoked", session.Revoked ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SessionRecord?> GetByIdHashAsync(string idHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id_hash, admin_id, created_utc, expires_utc, idle_expires_utc, revoked
            FROM sessions WHERE id_hash = $id_hash;
            """;
        command.Parameters.AddWithValue("$id_hash", idHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new SessionRecord
        {
            IdHash = reader.GetString(0),
            AdminId = reader.GetString(1),
            CreatedUtc = ParseUtc(reader.GetString(2)),
            ExpiresUtc = ParseUtc(reader.GetString(3)),
            IdleExpiresUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
            Revoked = reader.GetInt64(5) != 0,
        };
    }

    /// <summary>Parses an ISO-8601 timestamp and normalizes to UTC kind.</summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, styles: System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public async Task RevokeAsync(string idHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET revoked = 1 WHERE id_hash = $id_hash;";
        command.Parameters.AddWithValue("$id_hash", idHash);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PruneExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sessions
            WHERE revoked = 1
               OR expires_utc <= $now
               OR (idle_expires_utc IS NOT NULL AND idle_expires_utc <= $now);
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
