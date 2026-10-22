using Microsoft.AspNetCore.Http;

namespace HostingAdmin.Api.Sessions;

/// <summary>
/// Issues and clears the opaque session cookie with the mandatory security
/// attributes (plan §3.5): HttpOnly, Secure, SameSite=Strict. The raw session id
/// is carried by the cookie only — never in a response body or log (T11, D7).
/// Login (task 6) reuses this helper; rotation on login means a fresh id is
/// issued through <see cref="Issue"/> on every successful authentication.
/// </summary>
public static class SessionCookieWriter
{
    public static void Issue(HttpContext context, string cookieName, string rawId, TimeSpan lifetime)
    {
        context.Response.Cookies.Append(cookieName, rawId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = lifetime,
            IsEssential = true,
        });
    }

    /// <summary>
    /// Clears the session cookie (logout/revocation path): the response carries
    /// an expired cookie so the browser drops it immediately.
    /// </summary>
    public static void Clear(HttpContext context, string cookieName)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}
