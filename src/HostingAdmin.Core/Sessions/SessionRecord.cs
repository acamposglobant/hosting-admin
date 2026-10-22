namespace HostingAdmin.Core.Sessions;

/// <summary>
/// A server-side session row. The database stores only the session-id
/// <see cref="IdHash"/> (SHA-256, hashed at rest) — never the raw id (T11).
/// </summary>
public sealed record SessionRecord
{
    /// <summary>SHA-256 hash (hex) of the raw opaque session id.</summary>
    public required string IdHash { get; init; }

    /// <summary>Administrator identifier associated with this session.</summary>
    public required string AdminId { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>Absolute expiration, enforced server-side (T8).</summary>
    public required DateTime ExpiresUtc { get; init; }

    /// <summary>Idle expiration, if enabled; null when off (plan §3.5).</summary>
    public DateTime? IdleExpiresUtc { get; init; }

    public required bool Revoked { get; init; }
}
