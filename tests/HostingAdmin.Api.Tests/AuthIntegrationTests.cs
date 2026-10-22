using System.Net;
using System.Text;
using System.Text.Json;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Auth;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Authentication integration tests (plan task 6, T12–T14): login/logout on top
/// of the session lifecycle, rate limiting, and audit of auth events.
/// Each test uses its own temporary SQLite database seeded with a PBKDF2
/// credential hash for the test password.
/// </summary>
public class AuthIntegrationTests
{
    private const string CookieName = "ha_session";
    private const string Password = "integration-test-password";

    private static readonly string PasswordHash = PasswordHasher.Hash(Password);

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost()
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"auth-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            // Small limits so the 429 test needs only 3 attempts.
            builder.UseSetting("Auth:MaxFailedLoginAttempts", "3");
            builder.UseSetting("Auth:LockoutWindow", "00:15:00");
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Automatic cookie handling rewrites Set-Cookie headers; tests set
            // the Cookie header explicitly.
            HandleCookies = false,
        });

        // Seed the admin credential (first-admin bootstrap is task 8).
        using (var scope = factory.Services.CreateScope())
        {
            var credentialStore = scope.ServiceProvider.GetRequiredService<IAdminCredentialStore>();
            credentialStore.SetCredentialHashAsync(PasswordHash).GetAwaiter().GetResult();
        }

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

    private static StringContent JsonBody(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static string? ExtractRawId(HttpResponseMessage response)
    {
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"{CookieName}="))
            : null;
        if (setCookie is null)
        {
            return null;
        }

        var value = setCookie[$"{CookieName}=".Length..];
        var end = value.IndexOf(';');
        return end < 0 ? value : value[..end];
    }

    private static async Task<int> CountAuditRowsAsync(WebApplicationFactory<Program> factory, string action)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = GetDatabasePath(factory),
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = $action;";
        command.Parameters.AddWithValue("$action", action);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static string GetDatabasePath(WebApplicationFactory<Program> factory)
    {
        // The path was provided via UseSetting; read it back from configuration.
        return factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value.Path;
    }

    [Fact]
    public async Task Login_WithCorrectPassword_SetsSecureCookie_AndAuditsSuccess()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/auth/login", JsonBody(new { password = Password }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.StartsWith($"{CookieName}=", setCookie);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

            // Exactly one audit row for the successful login (T14).
            Assert.Equal(1, await CountAuditRowsAsync(factory, "auth.login"));
            Assert.Equal(0, await CountAuditRowsAsync(factory, "auth.login_failed"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401Envelope_AndAuditsFailure()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/auth/login", JsonBody(new { password = "wrong" }));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("unauthorized", body);

            // Exactly one audit row for the failed login; the wrong password
            // must not appear anywhere in the audit trail (D7).
            Assert.Equal(1, await CountAuditRowsAsync(factory, "auth.login_failed"));
            Assert.Equal(0, await CountAuditRowsAsync(factory, "auth.login"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Login_WithMissingPassword_Returns400()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/auth/login", JsonBody(new { password = "" }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await CountAuditRowsAsync(factory, "auth.login"));
            Assert.Equal(0, await CountAuditRowsAsync(factory, "auth.login_failed"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Login_AfterMaxFailures_Returns429()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var failed = await client.PostAsync("/api/auth/login", JsonBody(new { password = "wrong" }));
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }

            // The next attempt — even with the correct password — is rate limited.
            var limited = await client.PostAsync("/api/auth/login", JsonBody(new { password = Password }));

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            var body = await limited.Content.ReadAsStringAsync();
            Assert.Contains("rate_limited", body);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Login_LockoutIsPersisted_SurvivesNewHost()
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hostingadmin-tests", $"auth-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        try
        {
            async Task<WebApplicationFactory<Program>> StartHost()
            {
                var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                {
                    b.UseSetting("Database:Path", databasePath);
                    b.UseSetting("Auth:MaxFailedLoginAttempts", "1");
                });
                using var scope = f.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAdminCredentialStore>()
                    .SetCredentialHashAsync(PasswordHash);
                return f;
            }

            var first = await StartHost();
            var firstClient = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var failed = await firstClient.PostAsync("/api/auth/login", JsonBody(new { password = "wrong" }));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            firstClient.Dispose();
            first.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // A brand-new host (fresh process state) must still see the lockout.
            var second = await StartHost();
            var secondClient = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var locked = await secondClient.PostAsync("/api/auth/login", JsonBody(new { password = Password }));
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
            secondClient.Dispose();
            second.Dispose();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Logout_WithValidSession_RevokesClearsCookie_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var login = await client.PostAsync("/api/auth/login", JsonBody(new { password = Password }));
            var rawId = ExtractRawId(login);
            Assert.NotNull(rawId);
            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={rawId}");

            var logout = await client.PostAsync("/api/auth/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

            // The response clears the cookie.
            var clearCookie = logout.Headers.GetValues("Set-Cookie").Single();
            Assert.Contains("expires=", clearCookie, StringComparison.OrdinalIgnoreCase);

            // The revoked session is rejected immediately (T9): a second logout
            // with the same cookie finds no valid session.
            var repeat = await client.PostAsync("/api/auth/logout", content: null);
            Assert.Equal(HttpStatusCode.Unauthorized, repeat.StatusCode);

            // Exactly one audit row for the logout (T14).
            Assert.Equal(1, await CountAuditRowsAsync(factory, "auth.logout"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Logout_WithoutSession_Returns401Envelope()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/auth/logout", content: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("unauthenticated", body);
            Assert.Equal(0, await CountAuditRowsAsync(factory, "auth.logout"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Login_NeverReturnsPasswordOrHash_InBodyOrDatabase()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.PostAsync("/api/auth/login", JsonBody(new { password = Password }));
            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(Password, body);
            Assert.DoesNotContain(PasswordHash, body);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var databaseText = File.ReadAllText(databasePath);
            Assert.DoesNotContain(Password, databaseText);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }
}
