namespace HostingAdmin.Core.Sessions;

/// <summary>
/// Session lifecycle configuration (plan §3.5). Absolute expiration is always
/// enforced server-side; idle expiration is optional and off by default.
/// </summary>
public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>Absolute session lifetime. Default 12 hours.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Idle expiration window. Null disables idle expiration (default for
    /// increment 1). Must be shorter than <see cref="AbsoluteLifetime"/> when set.
    /// </summary>
    public TimeSpan? IdleTimeout { get; set; }

    /// <summary>Cookie name for the opaque session id.</summary>
    public string CookieName { get; set; } = "ha_session";
}
