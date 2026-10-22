using System.Net;
using System.Text;
using System.Text.Json;
using HostingAdmin.Core.Auth;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for CSRF protection and Origin validation (plan task 7,
/// D4, T12/T13). Each test uses its own temporary SQLite database file.
/// </summary>
public class CsrfOriginIntegrationTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";
    private const string AllowedOrigin = "https://panel.local";
    private const string EvilOrigin = "https://evil.example";
    private const string Password = "correct horse battery staple";

    private static readonly string TempDirectory =
        Path.Combine(Path.GetTempPath(), "hostingadmin-tests");

    private static string NewDatabasePath()
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, $"csrf-{Guid.NewGuid():N}.db");
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost()
    {
        var databasePath = NewDatabasePath();
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
                builder.UseSetting("Database:Path", databasePath);
                // Memory configuration binds arrays by indexed keys.
                builder.UseSetting("Security:AllowedOrigins:0", AllowedOrigin);
            });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Automatic cookie handling rewrites Set-Cookie headers and refuses
            // Secure cookies over http; tests set the Cookie header explicitly.
            HandleCookies = false,
        });
        return (factory, client, databasePath);
    }

    private static void DisposeHost(WebApplicationFactory<Program> factory, HttpClient client, string databasePath)
    {
        client.Dispose();
        factory.Dispose();
        // SQLite connection pooling keeps the temp file open; release before delete.
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }

    private static string GetSetCookie(HttpResponseMessage response, string cookieName)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"{cookieName}=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(header);
        return header!;
    }

    private static string? TryGetSetCookie(HttpResponseMessage response, string cookieName)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"{cookieName}=", StringComparison.Ordinal))
            : null;
    }

    private static string ExtractToken(string setCookieHeader)
    {
        var start = setCookieHeader.IndexOf('=') + 1;
        var end = setCookieHeader.IndexOf(';', start);
        return end < 0 ? setCookieHeader[start..] : setCookieHeader[start..end];
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>
    /// Issues a session + CSRF cookie pair (the create POST requires an allowed
    /// Origin) and returns the raw Set-Cookie headers for both cookies.
    /// </summary>
    private static async Task<(string SessionCookie, string CsrfCookie)> IssueSessionAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
        request.Headers.Add("Origin", AllowedOrigin);
        var create = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var sessionCookie = GetSetCookie(create, SessionCookieName);
        var csrfCookie = GetSetCookie(create, CsrfCookieName);
        return (sessionCookie, csrfCookie);
    }

    /// <summary>Seeds a PBKDF2 credential directly into the test database.</summary>
    private static void SeedCredential(WebApplicationFactory<Program> factory, string databasePath)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdminCredentialStore>();
        store.SetCredentialHashAsync(PasswordHasher.Hash(Password)).GetAwaiter().GetResult();
    }

    // ---- T12: cookie-authenticated requests without a valid CSRF token ----

    [Fact]
    public async Task Get_WithSessionButNoCsrfToken_IsRejected()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfCookie) = await IssueSessionAsync(client);

            // Session cookie present, but neither CSRF cookie nor header sent.
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/sessions/current");
            request.Headers.Add("Cookie", $"{sessionCookie}");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("csrf_failed", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Get_WithSessionAndWrongCsrfToken_IsRejected()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfCookie) = await IssueSessionAsync(client);

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/sessions/current");
            request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
            request.Headers.Add(CsrfHeaderName, new string('0', 64));
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Get_WithSessionAndValidCsrfToken_Succeeds()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfCookie) = await IssueSessionAsync(client);

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/sessions/current");
            request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
            request.Headers.Add(CsrfHeaderName, ExtractToken(csrfCookie));
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Post_WithSessionButMissingCsrfCookie_IsRejected()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfCookie) = await IssueSessionAsync(client);

            // Session cookie + header, but the CSRF cookie itself is missing:
            // double-submit requires both sides to match — fail closed.
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions/invalidate");
            request.Headers.Add("Cookie", sessionCookie);
            request.Headers.Add(CsrfHeaderName, ExtractToken(csrfCookie));
            request.Headers.Add("Origin", AllowedOrigin);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("csrf_failed", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // ---- T13: cross-origin requests with a disallowed Origin ----

    [Fact]
    public async Task Post_WithDisallowedOrigin_IsRejectedWithoutCorsHeader()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
            request.Headers.Add("Origin", EvilOrigin);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("origin_rejected", await response.Content.ReadAsStringAsync());

            // The rejected origin is never reflected back (T13).
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Post_WithoutOrigin_IsRejected()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/test/sessions", content: null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("origin_rejected", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Post_WithAllowedOrigin_PassesOriginValidation()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
            request.Headers.Add("Origin", AllowedOrigin);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Get_WithDisallowedOrigin_IsRejected()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            // Health is exempt from Origin validation.
            var health = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            health.Headers.Add("Origin", EvilOrigin);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(health)).StatusCode);

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/sessions/current");
            request.Headers.Add("Origin", EvilOrigin);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // ---- Login/logout integration with CSRF ----

    [Fact]
    public async Task Login_RequiresAllowedOrigin_AndIssuesCsrfCookie()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            SeedCredential(factory, databasePath);

            var evil = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonBody(new { password = Password }),
            };
            evil.Headers.Add("Origin", EvilOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(evil)).StatusCode);

            var missing = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonBody(new { password = Password }),
            };
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(missing)).StatusCode);

            var allowed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonBody(new { password = Password }),
            };
            allowed.Headers.Add("Origin", AllowedOrigin);
            var ok = await client.SendAsync(allowed);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // Login issues both the session and the CSRF cookie.
            Assert.NotNull(TryGetSetCookie(ok, SessionCookieName));
            Assert.NotNull(TryGetSetCookie(ok, CsrfCookieName));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Logout_RequiresCsrfToken_AndClearsBothCookies()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            SeedCredential(factory, databasePath);

            var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonBody(new { password = Password }),
            };
            login.Headers.Add("Origin", AllowedOrigin);
            var loginResponse = await client.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var sessionCookie = GetSetCookie(loginResponse, SessionCookieName);
            var csrfCookie = GetSetCookie(loginResponse, CsrfCookieName);

            // Tokenless logout with a session cookie: rejected (T12).
            var tokenless = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            tokenless.Headers.Add("Cookie", sessionCookie);
            tokenless.Headers.Add("Origin", AllowedOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(tokenless)).StatusCode);

            // With the token: succeeds and both cookies are cleared (expired).
            var valid = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            valid.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
            valid.Headers.Add(CsrfHeaderName, ExtractToken(csrfCookie));
            valid.Headers.Add("Origin", AllowedOrigin);
            var ok = await client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.NotNull(TryGetSetCookie(ok, SessionCookieName));
            Assert.NotNull(TryGetSetCookie(ok, CsrfCookieName));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CsrfRejections_AreAudited()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
            request.Headers.Add("Origin", EvilOrigin);
            await client.SendAsync(request);

            Assert.Equal(1, await CountAuditRowsAsync(databasePath, "authz.origin_rejected"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    private static async Task<int> CountAuditRowsAsync(string databasePath, string action)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = $action";
        command.Parameters.AddWithValue("$action", action);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
