using HostingAdmin.Core.Bootstrap;
using Microsoft.Data.Sqlite;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed bootstrap state store: the bootstrap lifecycle (plan D6) lives
/// in the admin_state table under the 'bootstrap.*' keys. Parameterized queries
/// only. Completion is durable across restarts (T7).
/// </summary>
public sealed class SqliteBootstrapStateStore(SqliteConnectionFactory connectionFactory) : IBootstrapStateStore
{
    private const string StatusKey = "bootstrap.status";
    private const string WindowExpiresKey = "bootstrap.window_expires_utc";

    public async Task<BootstrapState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM admin_state WHERE key IN ($statusKey, $expiresKey);";
        command.Parameters.AddWithValue("$statusKey", StatusKey);
        command.Parameters.AddWithValue("$expiresKey", WindowExpiresKey);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        if (!values.TryGetValue(StatusKey, out var statusText) ||
            !Enum.TryParse<BootstrapStatus>(statusText, out var status))
        {
            return BootstrapState.NotConfiguredState;
        }

        DateTime? windowExpiresUtc = null;
        if (status == BootstrapStatus.WindowOpen &&
            values.TryGetValue(WindowExpiresKey, out var expiresText) &&
            DateTime.TryParse(expiresText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            windowExpiresUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return status switch
        {
            BootstrapStatus.Completed => BootstrapState.CompletedState,
            BootstrapStatus.WindowOpen when windowExpiresUtc is not null => BootstrapState.OpenWindow(windowExpiresUtc.Value),
            _ => BootstrapState.NotConfiguredState,
        };
    }

    public async Task SetStateAsync(BootstrapState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var transaction = connection.BeginTransaction();

        await UpsertAsync(connection, transaction, StatusKey, state.Status.ToString(), cancellationToken);
        if (state.Status == BootstrapStatus.WindowOpen && state.WindowExpiresUtc is { } expires)
        {
            await UpsertAsync(connection, transaction, WindowExpiresKey, expires.ToString("O"), cancellationToken);
        }
        else
        {
            await DeleteAsync(connection, transaction, WindowExpiresKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO admin_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM admin_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
