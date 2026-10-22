using HostingAdmin.Core.Auth;
using Microsoft.Data.Sqlite;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed admin credential store: the PBKDF2 password hash lives in the
/// admin_state table (key 'admin.credential_hash'), populated by first-admin
/// bootstrap (task 8). Parameterized queries only; the hash is never logged (D7).
/// </summary>
public sealed class SqliteAdminCredentialStore(SqliteConnectionFactory connectionFactory) : IAdminCredentialStore
{
    private const string CredentialKey = "admin.credential_hash";

    public async Task<string?> GetCredentialHashAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM admin_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", CredentialKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string hash ? hash : null;
    }

    public async Task SetCredentialHashAsync(string credentialHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialHash);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", CredentialKey);
        command.Parameters.AddWithValue("$value", credentialHash);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
