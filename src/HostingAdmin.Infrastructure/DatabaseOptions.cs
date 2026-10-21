using Microsoft.Data.Sqlite;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// Options for the SQLite database. Validated at startup (engineering standards).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Absolute or relative path of the SQLite database file. Must be outside the web root in production.</summary>
    public required string Path { get; init; }
}
