using System.Text.Json;
using HostingAdmin.Core.Auth;
using Microsoft.Data.Sqlite;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed login throttle state (plan task 6: 429 after N attempts).
/// State is serialized as JSON into the admin_state table (key 'auth.throttle')
/// so lockouts survive process restarts. Parameterized queries only.
/// </summary>
public sealed class SqliteLoginThrottleStore(SqliteConnectionFactory connectionFactory) : ILoginThrottleStore
{
    private const string ThrottleKey = "auth.throttle";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed record ThrottleDto(int FailedCount, DateTime? WindowStartUtc, DateTime? LockedUntilUtc);

    public async Task<LoginThrottleState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM admin_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", ThrottleKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string json)
        {
            return LoginThrottleState.Empty;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ThrottleDto>(json, SerializerOptions);
            if (dto is null)
            {
                return LoginThrottleState.Empty;
            }

            return new LoginThrottleState
            {
                FailedCount = dto.FailedCount,
                WindowStartUtc = dto.WindowStartUtc,
                LockedUntilUtc = dto.LockedUntilUtc,
            };
        }
        catch (JsonException)
        {
            // Corrupt state must not brick login; treat as empty (fail open for
            // availability, fail closed for the credential check itself).
            return LoginThrottleState.Empty;
        }
    }

    public async Task SetStateAsync(LoginThrottleState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dto = new ThrottleDto(state.FailedCount, state.WindowStartUtc, state.LockedUntilUtc);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", ThrottleKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(dto, SerializerOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
