namespace HostingAdmin.Core.Auth;

/// <summary>
/// Authentication configuration (plan §3.3): single admin, PBKDF2 hash,
/// login rate limiting with generic failure messages.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Number of failed login attempts allowed within the lockout window before
    /// login is locked. Default 5 (plan task 6: 429 after N attempts).
    /// </summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>
    /// Length of the lockout applied after too many failures, and the window
    /// over which failures are counted. Default 15 minutes.
    /// </summary>
    public TimeSpan LockoutWindow { get; set; } = TimeSpan.FromMinutes(15);
}
