using HostingAdmin.Core.Sessions;

namespace HostingAdmin.Core.Sessions;

/// <summary>
/// Server-side session persistence. Implementations store only the session-id
/// hash, never the raw id (T11). Lookups by raw id hash the input first.
/// </summary>
public interface ISessionStore
{
    /// <summary>Inserts a new session row.</summary>
    Task InsertAsync(SessionRecord session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the session with the given id hash, or null when unknown.
    /// </summary>
    Task<SessionRecord?> GetByIdHashAsync(string idHash, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session with the given id hash (T9). Idempotent.</summary>
    Task RevokeAsync(string idHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes sessions that are revoked, past absolute expiration, or past
    /// idle expiration (when set). Returns the number of rows removed (T10).
    /// </summary>
    Task<int> PruneExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
