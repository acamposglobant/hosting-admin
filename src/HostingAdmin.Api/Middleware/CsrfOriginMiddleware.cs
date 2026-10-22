using System.Security.Cryptography;
using System.Text;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Security;
using HostingAdmin.Core.Sessions;
using Microsoft.AspNetCore.Http;

namespace HostingAdmin.Api.Middleware;

using SessionOptions = HostingAdmin.Core.Sessions.SessionOptions;

/// <summary>
/// Explicit CSRF protection and Origin validation for all cookie-authenticated
/// API requests (plan D4) — SameSite=Strict alone is not sufficient.
///
/// Double-submit pattern: a request that presents the session cookie must also
/// present the CSRF cookie and echo its value in the configured header; the two
/// are compared in constant time (via SHA-256, so arbitrary attacker-supplied
/// input can never throw or leak timing). Mutating requests additionally require
/// an <c>Origin</c> header matching one of the configured allowed origins; on
/// safe methods an Origin is validated only when present. Disallowed origins are
/// rejected without reflecting the origin back (no CORS header) — T13.
/// <c>/api/health</c> is exempt (no state, no session).
/// </summary>
public sealed class CsrfOriginMiddleware
{
    private const string HealthPath = "/api/health";

    private readonly RequestDelegate _next;

    public CsrfOriginMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        SecurityOptions securityOptions,
        SessionOptions sessionOptions,
        IAuditStore auditStore)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path == HealthPath ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var isMutation = !HttpMethods.IsGet(context.Request.Method) &&
                         !HttpMethods.IsHead(context.Request.Method);

        // Origin validation: required on mutations, validated when present otherwise.
        if ((isMutation || origin.Length > 0) && !IsOriginAllowed(origin, securityOptions))
        {
            await RejectAsync(context, auditStore, "authz.origin_rejected", "origin_rejected",
                hasSessionCookie: context.Request.Cookies.ContainsKey(sessionOptions.CookieName));
            return;
        }

        // CSRF token: required whenever the session cookie is presented (any
        // method, per the endpoint table's "session + CSRF"). Fail closed when
        // the session cookie is present but the CSRF cookie is missing.
        if (context.Request.Cookies.ContainsKey(sessionOptions.CookieName))
        {
            var cookieToken = context.Request.Cookies[securityOptions.CsrfCookieName] ?? string.Empty;
            var headerToken = context.Request.Headers[securityOptions.CsrfHeaderName].ToString();
            if (!TokensMatch(cookieToken, headerToken))
            {
                await RejectAsync(context, auditStore, "authz.csrf_rejected", "csrf_failed",
                    hasSessionCookie: true);
                return;
            }
        }

        await _next(context);
    }

    private static bool IsOriginAllowed(string origin, SecurityOptions options)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        foreach (var allowed in options.AllowedOrigins)
        {
            if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Constant-time token comparison via SHA-256 digests: safe for arbitrary
    /// attacker-controlled input (no parsing, no length leak beyond the digest).
    /// </summary>
    private static bool TokensMatch(string cookieToken, string headerToken)
    {
        if (cookieToken.Length == 0 || headerToken.Length == 0)
        {
            return false;
        }

        var cookieDigest = SHA256.HashData(Encoding.UTF8.GetBytes(cookieToken));
        var headerDigest = SHA256.HashData(Encoding.UTF8.GetBytes(headerToken));
        return CryptographicOperations.FixedTimeEquals(cookieDigest, headerDigest);
    }

    private static async Task RejectAsync(
        HttpContext context,
        IAuditStore auditStore,
        string auditAction,
        string errorCode,
        bool hasSessionCookie)
    {
        // Never reflect the rejected origin (no Access-Control-Allow-Origin).
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = hasSessionCookie ? "admin" : "anonymous",
            Action = auditAction,
            Outcome = AuditOutcomes.Failure,
            SourceIp = context.Connection.RemoteIpAddress?.ToString(),
            Detail = null,
        });

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { code = errorCode, message = "Request rejected." },
        });
    }
}
