using HostingAdmin.Infrastructure.Persistence;

namespace HostingAdmin.Worker;

/// <summary>
/// SQLite-backed cursor store (admin_state key 'audit.shipping.last_id').
/// Parameterized upsert, same pattern as the other admin_state stores.
/// </summary>
public sealed class SqliteAuditShippingCursorStore(SqliteConnectionFactory connectionFactory) : IAuditShippingCursorStore
{
    public const string CursorKey = "audit.shipping.last_id";

    public async Task<long> GetLastShippedIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM admin_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", CursorKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string value && long.TryParse(value, out var id) ? id : 0;
    }

    public async Task SetLastShippedIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", CursorKey);
        command.Parameters.AddWithValue("$value", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
