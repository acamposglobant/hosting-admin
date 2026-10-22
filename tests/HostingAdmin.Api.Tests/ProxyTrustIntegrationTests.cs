using System.Net;
using System.Text;
using System.Text.Json;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for trusted-proxy enforcement and header hygiene
/// (plan D2/D3, task 9, T5/T6), run in a production-like configuration
/// (Proxy:RequireAttestation=true) on CI. The remote address is controlled
/// per request via the X-Test-Remote-Ip header, which a test-only middleware
/// maps onto Connection.RemoteIpAddress — production code never reads it.
/// </summary>
public sealed class ProxyTrustIntegrationTests
{
    private const string AttestationSecret = "proxy-attestation-test-secret";
    private const string AttestationHeader = "X-Proxy-Attestation";

    /// <summary>Middleware (test host only) that fakes the connection's remote address.</summary>
    private sealed class TestRemoteIpMiddleware(RequestDelegate next)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var values) &&
                System.Net.IPAddress.TryParse(values.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await next(context);
        }
    }

    /// <summary>Inserts the remote-ip middleware before the API pipeline.</summary>
    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.UseMiddleware<TestRemoteIpMiddleware>();
                next(app);
            };
    }

    /// <summary>
    /// Creates a host in production-like proxy configuration (attestation required,
    /// secret set out-of-band as the deployment would). When <paramref name="requireAttestation"/>
    /// is false the default (development) configuration is exercised instead.
    /// </summary>
    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost(
        bool requireAttestation = true)
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"proxy-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
            if (requireAttestation)
            {
                builder.UseSetting("Proxy:RequireAttestation", "true");
                builder.UseSetting("Proxy:AttestationSecret", AttestationSecret);
            }

            builder.ConfigureServices(services =>
            {
                services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        // Default simulated source: the trusted loopback proxy (D2).
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "127.0.0.1");

        return (factory, client, databasePath);
    }

    private static void DisposeHost(WebApplicationFactory<Program> factory, HttpClient client, string databasePath)
    {
        client.Dispose();
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
        catch (IOException)
        {
            // Best effort only; the file is in the temp directory.
        }
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Origin", "https://panel.local");
        return request;
    }

    private static string GetDatabasePath(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value.Path;

    private static async Task<int> CountAuditRowsAsync(WebApplicationFactory<Program> factory, string action)
    {
        factory.Services.GetRequiredService<SqliteConnectionFactory>().EnsureInitialized();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = $action;";
        command.Parameters.AddWithValue("$action", action);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task<string?> GetLastAuditDetailAsync(WebApplicationFactory<Program> factory, string action)
    {
        factory.Services.GetRequiredService<SqliteConnectionFactory>().EnsureInitialized();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM audit_events WHERE action = $action ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$action", action);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>Creates a session via the test endpoint and returns its cookie value.</summary>
    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var response = await client.SendAsync(Post("/api/test/sessions"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies), "expected session cookie");
        var cookie = cookies!.First();
        return cookie.Split(';')[0];
    }

    [Fact]
    public async Task Attestation_Required_MissingHeader_Returns403_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Post("/api/auth/login", JsonBody(new { password = "x" })));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
            Assert.Equal("missing", await GetLastAuditDetailAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_Required_WrongSecret_Returns403_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Add(AttestationHeader, "wrong-secret");

            var response = await client.SendAsync(Post("/api/auth/login", JsonBody(new { password = "x" })));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
            Assert.Equal("invalid_secret", await GetLastAuditDetailAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_Required_CorrectSecret_FromNonTrustedIp_Returns403()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            // Attestation is only accepted from trusted proxy addresses (D3).
            client.DefaultRequestHeaders.Remove("X-Test-Remote-Ip");
            client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "203.0.113.50");
            client.DefaultRequestHeaders.Add(AttestationHeader, AttestationSecret);

            var response = await client.SendAsync(Post("/api/auth/login", JsonBody(new { password = "x" })));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_Required_CorrectSecret_FromTrustedProxy_Passes()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Add(AttestationHeader, AttestationSecret);

            var response = await client.SendAsync(Post("/api/auth/login", JsonBody(new { password = "x" })));

            // Not 403: the request passed proxy attestation (login itself fails on
            // the unknown password, which is out of scope here).
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_Required_ValidSessionCookie_WithoutHeader_StillRejected()
    {
        // T5: attestation applies even to requests with a valid session cookie.
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Add(AttestationHeader, AttestationSecret);
            var sessionCookie = await CreateSessionAsync(client);
            client.DefaultRequestHeaders.Remove(AttestationHeader);
            client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

            var response = await client.GetAsync("/api/test/sessions/current");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_NotRequired_ByDefault_RequestSucceeds()
    {
        var (factory, client, databasePath) = CreateHost(requireAttestation: false);
        try
        {
            var response = await client.SendAsync(Post("/api/test/sessions"));

            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Health_Endpoint_Exempt_FromAttestation()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.GetAsync("/api/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, await CountAuditRowsAsync(factory, "proxy.attestation_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task IdentityHeader_FromNonTrustedSource_IsStripped_AndAudited()
    {
        var (factory, client, databasePath) = CreateHost(requireAttestation: false);
        try
        {
            client.DefaultRequestHeaders.Remove("X-Test-Remote-Ip");
            client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "203.0.113.50");
            client.DefaultRequestHeaders.Add("X-Forwarded-User", "admin");

            var response = await client.SendAsync(Post("/api/test/sessions"));

            // The request itself proceeds (stripped, not rejected — availability),
            // but the spoof attempt is audited.
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "proxy.identity_header_stripped"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task IdentityHeader_FromTrustedProxy_IsNotStripped()
    {
        var (factory, client, databasePath) = CreateHost(requireAttestation: false);
        try
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-User", "admin");

            await client.SendAsync(Post("/api/test/sessions"));

            Assert.Equal(0, await CountAuditRowsAsync(factory, "proxy.identity_header_stripped"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task ForwardedFor_FromTrustedProxy_RewritesRemoteIp_BehindProxy()
    {
        // Production shape: proxy connects from loopback and forwards the real
        // client address. Bootstrap's local-only check (D6) must see the real
        // client — a remote attacker cannot activate bootstrap through the proxy.
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Add(AttestationHeader, AttestationSecret);
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.99");

            var response = await client.SendAsync(Post("/api/bootstrap/activate"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.activate_denied_nonlocal"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task ForwardedFor_FromNonTrustedSource_IsIgnored()
    {
        var (factory, client, databasePath) = CreateHost(requireAttestation: false);
        try
        {
            // Non-trusted source spoofing X-Forwarded-For: 127.0.0.1 must not be
            // able to appear local — the header is stripped before any check.
            client.DefaultRequestHeaders.Remove("X-Test-Remote-Ip");
            client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "203.0.113.50");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");

            var response = await client.SendAsync(Post("/api/bootstrap/activate"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.activate_denied_nonlocal"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Attestation_Rejection_AuditsSourceIp()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Remove("X-Test-Remote-Ip");
            client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "203.0.113.77");

            await client.SendAsync(Post("/api/auth/login", JsonBody(new { password = "x" })));

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_ip FROM audit_events WHERE action = 'proxy.attestation_rejected' ORDER BY id DESC LIMIT 1;";
            var sourceIp = await command.ExecuteScalarAsync();
            Assert.Equal("203.0.113.77", sourceIp as string);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }
}
