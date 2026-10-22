using System.Security.Cryptography;

namespace HostingAdmin.Core.Sessions;

/// <summary>
/// Session lifecycle logic (plan §3.5, T8–T11).
/// - Identifiers are opaque, from a cryptographic RNG (≥ 128 bits entropy).
/// - Only the SHA-256 hash of the id is ever persisted or logged (T11, D7);
///   a database leak does not yield usable session cookies.
/// - Absolute expiration is always enforced; idle expiration is optional (T8).
/// - Revocation is immediate and permanent (T9).
/// </summary>
public sealed class SessionService(SessionOptions options)
{
    /// <summary>Minimum entropy in bits for the raw session id (plan §3.5).</summary>
    public const int MinimumIdEntropyBits = 128;

    /// <summary>Number of random bytes: 32 bytes = 256 bits of entropy.</summary>
    private const int IdByteCount = 32;

    /// <summary>
    /// Creates a new session for the administrator. Returns the raw id (to be
    /// carried by the cookie only) and the record to persist (hash at rest).
    /// </summary>
    public (string RawId, SessionRecord Record) Create(string adminId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(adminId))
        {
            throw new ArgumentException("AdminId is required.", nameof(adminId));
        }

        var rawId = Convert.ToHexString(RandomNumberGenerator.GetBytes(IdByteCount));

        var record = new SessionRecord
        {
            IdHash = HashId(rawId),
            AdminId = adminId,
            CreatedUtc = nowUtc,
            ExpiresUtc = nowUtc + options.AbsoluteLifetime,
            IdleExpiresUtc = options.IdleTimeout is { } idle ? nowUtc + idle : null,
            Revoked = false,
        };

        return (rawId, record);
    }

    /// <summary>
    /// Validates a session record against the current time: not revoked, not
    /// past absolute expiration, and not past idle expiration when enabled.
    /// </summary>
    public static bool IsValid(SessionRecord record, DateTime nowUtc) =>
        !record.Revoked
        && nowUtc < record.ExpiresUtc
        && (record.IdleExpiresUtc is null || nowUtc < record.IdleExpiresUtc);

    /// <summary>SHA-256 hex hash of a raw session id (hashed at rest, T11).</summary>
    public static string HashId(string rawId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawId)));
}
