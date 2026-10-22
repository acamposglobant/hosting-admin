namespace HostingAdmin.Core.Security;

/// <summary>
/// CSRF and Origin validation configuration (plan D4). <see cref="AllowedOrigins"/>
/// must be explicitly configured — an empty or placeholder list fails closed,
/// because every mutating request requires an allowed Origin header.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Exact origins (scheme + host [+ port]) allowed to call the API.</summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>Double-submit CSRF cookie name. Not HttpOnly: the SPA must read it.</summary>
    public string CsrfCookieName { get; init; } = "ha_csrf";

    /// <summary>Header the client must send with the CSRF token value.</summary>
    public string CsrfHeaderName { get; init; } = "X-CSRF-Token";
}
