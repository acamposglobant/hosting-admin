using HostingAdmin.Core.Services;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite persistence for pending destructive service-action confirmations
/// (plan §3.4/§3.5, D20). Only the SHA-256 token hash is stored, never the raw
/// token (D25). All queries are parameterized.
/// </summary>
public sealed class SqliteServiceActionConfirmationStore(
    SqliteConnectionFactory connectionFactory,
    ILogger<SqliteServiceActionConfirmationStore> logger) : IServiceActionConfirmationStore
{
    public async Task InsertAsync(ServiceActionConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO service_action_confirmations
                (id_hash, unit, action, session_hash, created_utc, expires_utc, consumed)
            VALUES ($id_hash, $unit, $action, $session_hash, $created_utc, $expires_utc, $consumed)
            """;
        command.Parameters.AddWithValue("$id_hash", confirmation.IdHash);
        command.Parameters.AddWithValue("$unit", confirmation.Unit);
        command.Parameters.AddWithValue("$action", confirmation.Action);
        command.Parameters.AddWithValue("$session_hash", confirmation.SessionHash);
        command.Parameters.AddWithValue("$created_utc", confirmation.CreatedUtc.ToString("o"));
        command.Parameters.AddWithValue("$expires_utc", confirmation.ExpiresUtc.ToString("o"));
        command.Parameters.AddWithValue("$consumed", confirmation.Consumed ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ServiceActionConfirmation?> GetByIdHashAsync(
        string idHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id_hash, unit, action, session_hash, created_utc, expires_utc, consumed
            FROM service_action_confirmations
            WHERE id_hash = $id_hash
            """;
        command.Parameters.AddWithValue("$id_hash", idHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapRow(reader);
    }

    public async Task<bool> TryConsumeAsync(string idHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        // Atomic single-use: the WHERE clause makes a concurrent duplicate
        // consume a no-op (plan §3.4: consumed on confirm AND on failure).
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE service_action_confirmations
            SET consumed = 1
            WHERE id_hash = $id_hash AND consumed = 0
            """;
        command.Parameters.AddWithValue("$id_hash", idHash);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task DeleteAsync(string idHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM service_action_confirmations WHERE id_hash = $id_hash";
        command.Parameters.AddWithValue("$id_hash", idHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PruneExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM service_action_confirmations WHERE expires_utc <= $now";
        command.Parameters.AddWithValue("$now", nowUtc.ToString("o"));

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows > 0)
        {
            logger.LogInformation("Pruned {Count} expired service-action confirmations", rows);
        }

        return rows;
    }

    private static ServiceActionConfirmation MapRow(System.Data.Common.DbDataReader reader) => new(
        IdHash: reader.GetString(0),
        Unit: reader.GetString(1),
        Action: reader.GetString(2),
        SessionHash: reader.GetString(3),
        CreatedUtc: DateTime.Parse(reader.GetString(4), styles: System.Globalization.DateTimeStyles.RoundtripKind),
        ExpiresUtc: DateTime.Parse(reader.GetString(5), styles: System.Globalization.DateTimeStyles.RoundtripKind),
        Consumed: reader.GetInt64(6) != 0);
}
