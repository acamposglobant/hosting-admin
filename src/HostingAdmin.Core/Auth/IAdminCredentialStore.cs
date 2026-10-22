namespace HostingAdmin.Core.Auth;

/// <summary>
/// Persists the administrator's password hash (PBKDF2-encoded, see
/// <see cref="PasswordHasher"/>). Populated by first-admin bootstrap (task 8);
/// until then no credential exists and login fails generically.
/// Implementations must never log or return the hash outside this interface (D7).
/// </summary>
public interface IAdminCredentialStore
{
    /// <summary>Returns the stored password hash, or null when no admin credential exists yet.</summary>
    Task<string?> GetCredentialHashAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets (or replaces) the admin credential hash. Used by bootstrap (task 8).</summary>
    Task SetCredentialHashAsync(string credentialHash, CancellationToken cancellationToken = default);
}
