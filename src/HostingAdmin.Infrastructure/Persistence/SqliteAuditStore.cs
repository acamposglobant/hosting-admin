using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed append-only audit store (plan §4).
/// All statements are parameterized; detail fields are scrubbed of secret
/// material before persistence (D7, defense in depth — callers must not log
/// secrets in the first place).
/// </summary>
public sealed class SqliteAuditStore(SqliteConnectionFactory connectionFactory, ILogger<SqliteAuditStore> logger) : IAuditStore
{
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var scrubbedDetail = auditEvent.Detail is null
            ? null
            : SecretRedactor.RedactJsonString(auditEvent.Detail);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (timestamp_utc, actor, action, outcome, source_ip, detail)
            VALUES ($timestamp_utc, $actor, $action, $outcome, $source_ip, $detail);
            """;

        command.Parameters.AddWithValue("$timestamp_utc", auditEvent.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$actor", auditEvent.Actor);
        command.Parameters.AddWithValue("$action", auditEvent.Action);
        command.Parameters.AddWithValue("$outcome", auditEvent.Outcome);
        command.Parameters.AddWithValue("$source_ip", (object?)auditEvent.SourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)scrubbedDetail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit_events WHERE timestamp_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("o"));

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation("Audit retention prune removed {Count} events older than {Cutoff:O}", deleted, cutoffUtc);
        }

        return deleted;
    }
}
