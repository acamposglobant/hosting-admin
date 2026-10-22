using Microsoft.AspNetCore.Http;

namespace HostingAdmin.Api.Security;

/// <summary>
/// Local-only authorization (plan D6): validates the ACTUAL remote connection
/// address (<see cref="HttpContext.Connection"/> RemoteIpAddress). Forwarded
/// headers are never consulted — a client-supplied X-Forwarded-For can never
/// grant local access.
/// </summary>
public static class LocalAccess
{
    public static bool IsLoopbackRemote(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && System.Net.IPAddress.IsLoopback(remote);
    }
}
