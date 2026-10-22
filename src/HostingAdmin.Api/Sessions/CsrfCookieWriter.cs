using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace HostingAdmin.Api.Sessions;

/// <summary>
/// Issues and clears the double-submit CSRF cookie (plan D4). The token is a
/// cryptographically random value carried by a Secure, SameSite=Strict cookie
/// that is deliberately NOT HttpOnly — the SPA JavaScript reads it and echoes
/// it back in the configured header on every cookie-authenticated request.
/// The server compares header and cookie with a constant-time comparison.
/// </summary>
public static class CsrfCookieWriter
{
    public static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public static void Issue(HttpContext context, string cookieName, string token, TimeSpan lifetime)
    {
        context.Response.Cookies.Append(cookieName, token, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = lifetime,
            IsEssential = true,
        });
    }

    public static void Clear(HttpContext context, string cookieName)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}
