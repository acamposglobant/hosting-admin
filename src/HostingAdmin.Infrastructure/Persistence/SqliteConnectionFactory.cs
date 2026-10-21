using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// Creates configured SQLite connections and ensures the schema exists.
/// The schema initializer is idempotent (CREATE TABLE IF NOT EXISTS) and enables
/// WAL mode (plan §4). All queries in the application are parameterized.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;
    private readonly ILogger<SqliteConnectionFactory> _logger;
    private readonly object _initLock = new();
    private bool _initialized;

    public SqliteConnectionFactory(DatabaseOptions options, ILogger<SqliteConnectionFactory> logger)
    {
        if (string.IsNullOrWhiteSpace(options.Path))
        {
            throw new InvalidOperationException("Database:Path must be configured.");
        }

        _databasePath = System.IO.Path.GetFullPath(options.Path);

        var directory = System.IO.Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _logger = logger;
    }

    /// <summary>Connection string, exposed for diagnostics and tests.</summary>
    public string ConnectionString { get; }

    /// <summary>Opens a connection with WAL mode applied.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // WAL mode improves concurrent read/write behavior (plan §4).
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await pragmaCommand.ExecuteNonQueryAsync();

        return connection;
    }

    /// <summary>
    /// Creates the schema if it does not exist. Idempotent: safe to call on every
    /// startup and from multiple tests against the same file.
    /// </summary>
    public void EnsureInitialized()
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS audit_events (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT    NOT NULL,
                    actor         TEXT    NOT NULL,
                    action        TEXT    NOT NULL,
                    outcome       TEXT    NOT NULL,
                    source_ip     TEXT,
                    detail        TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_audit_events_timestamp ON audit_events (timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_audit_events_action    ON audit_events (action);

                CREATE TABLE IF NOT EXISTS admin_state (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sessions (
                    id_hash          TEXT PRIMARY KEY,
                    admin_id         TEXT NOT NULL,
                    created_utc      TEXT NOT NULL,
                    expires_utc      TEXT NOT NULL,
                    idle_expires_utc TEXT,
                    revoked          INTEGER NOT NULL DEFAULT 0
                );
                """;
            command.ExecuteNonQuery();

            _initialized = true;
            _logger.LogInformation("Database schema initialized at {Path}", _databasePath);
        }
    }
}
