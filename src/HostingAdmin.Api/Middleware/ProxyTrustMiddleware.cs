using System.Security.Cryptography;
using System.Text;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Proxy;
using Microsoft.AspNetCore.Http;

namespace HostingAdmin.Api.Middleware;

/// <summary>
/// Trusted-proxy enforcement and header hygiene (plan D2/D3, task 9, T5/T6).
///
/// Attestation (defense in depth, D2): when <see cref="ProxyOptions.RequireAttestation"/>
/// is enabled, requests must originate from a trusted proxy address and carry the
/// shared-secret attestation header — including requests with a valid session
/// cookie (T5). The liveness probe is exempt (local systemd check, no data).
///
/// Header hygiene (D3): forwarded and identity headers from any other source are
/// stripped before application code runs; client-supplied identity headers are
/// never trusted. Forwarded-For from a trusted proxy rewrites the effective
/// remote address, so downstream local-only checks (D6) and audit source IPs see
/// the real client, not the proxy.
/// </summary>
public sealed class ProxyTrustMiddleware(RequestDelegate next, ProxyOptions options)
{
    /// <summary>Headers a client must never be able to inject (D3).</summary>
    private static readonly string[] ForwardedHeaders =
    [
        "X-Forwarded-For",
        "X-Forwarded-Proto",
        "X-Forwarded-Host",
    ];

    private static readonly string[] IdentityHeaders =
    [
        "X-Forwarded-User",
        "X-Forwarded-Email",
        "X-Forwarded-Preferred-Username",
    ];

    public async Task InvokeAsync(HttpContext context, IAuditStore auditStore)
    {
        if (IsLivenessProbe(context.Request))
        {
            await next(context);
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        var trustedSource = remoteAddress is not null && options.TrustedProxyAddresses
            .Select(address => System.Net.IPAddress.TryParse(address, out var parsed) ? parsed : null)
            .Any(address => address is not null && address.Equals(remoteAddress));

        if (options.RequireAttestation)
        {
            if (!trustedSource)
            {
                await AuditRejectionAsync(auditStore, context, "untrusted_source");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = HostingAdmin.Core.ErrorHandling.ErrorCodes.Forbidden, message = "Request rejected." },
                });
                return;
            }

            if (!context.Request.Headers.TryGetValue(options.AttestationHeaderName, out var attestationValues))
            {
                await AuditRejectionAsync(auditStore, context, "missing");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = HostingAdmin.Core.ErrorHandling.ErrorCodes.Forbidden, message = "Request rejected." },
                });
                return;
            }

            var provided = attestationValues.ToString();
            if (!SecretMatches(provided))
            {
                await AuditRejectionAsync(auditStore, context, "invalid_secret");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = HostingAdmin.Core.ErrorHandling.ErrorCodes.Forbidden, message = "Request rejected." },
                });
                return;
            }
        }

        if (!trustedSource)
        {
            // D3: the client is not a trusted proxy — strip forwarded and identity
            // headers so no downstream code can ever act on them.
            var strippedIdentityHeader = false;
            foreach (var headerName in ForwardedHeaders)
            {
                context.Request.Headers.Remove(headerName);
            }

            foreach (var headerName in IdentityHeaders)
            {
                if (context.Request.Headers.ContainsKey(headerName))
                {
                    context.Request.Headers.Remove(headerName);
                    strippedIdentityHeader = true;
                }
            }

            context.Request.Headers.Remove(options.AttestationHeaderName);

            if (strippedIdentityHeader)
            {
                await auditStore.AppendAsync(new AuditEvent
                {
                    TimestampUtc = DateTime.UtcNow,
                    Actor = "anonymous",
                    Action = "proxy.identity_header_stripped",
                    Outcome = AuditOutcomes.Failure,
                    SourceIp = remoteAddress?.ToString(),
                    Detail = null,
                });
            }
        }
        else if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedForValues))
        {
            // Trusted proxy: adopt the rightmost forwarded address (the one our
            // proxy appended) as the effective remote address. Downstream checks
            // (bootstrap D6, audit source) then see the real client.
            var rightmost = forwardedForValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (rightmost is not null && System.Net.IPAddress.TryParse(rightmost, out var clientAddress))
            {
                context.Connection.RemoteIpAddress = clientAddress;
            }
        }

        await next(context);
    }

    private static bool IsLivenessProbe(HttpRequest request) =>
        request.Path == "/api/health";

    /// <summary>Constant-time comparison; never logs the secret or the provided value (D7).</summary>
    private bool SecretMatches(string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(options.AttestationSecret);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static async Task AuditRejectionAsync(IAuditStore auditStore, HttpContext context, string reason)
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = "anonymous",
            Action = "proxy.attestation_rejected",
            Outcome = AuditOutcomes.Failure,
            SourceIp = context.Connection.RemoteIpAddress?.ToString(),
            Detail = reason,
        });
    }
}
