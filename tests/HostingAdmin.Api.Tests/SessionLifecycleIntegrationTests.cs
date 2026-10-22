using System.Net;
using System.Text.Json;
using HostingAdmin.Core.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Session lifecycle integration tests (plan task 5, T8–T11), exercised through
/// the double-gated test-only session endpoints (Diagnostics:TestSessionsEnabled,
/// non-production only — the same gate pattern as the forced-error endpoint).
/// Each test uses its own temporary SQLite database file.
/// </summary>
public class SessionLifecycleIntegrationTests
{
    private const string CookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";

    private sealed record TestBody(string? Status, string? Error);

    /// <summary>
    /// Disposes the host and best-effort deletes the temporary database. SQLite
    /// connection pooling can hold the file briefly after disposal, so deletion
    /// failures are ignored (files live in the temp directory).
    /// </summary>
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

    private static async Task<string> CreateTempDatabasePathAsync()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"sessions-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return await Task.FromResult(path);
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost()
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"sessions-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
        });
        return (factory, factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Automatic cookie handling rewrites Set-Cookie headers and refuses
            // Secure cookies over http; tests set the Cookie header explicitly.
            HandleCookies = false,
        }), databasePath);
    }

    private static async Task<TestBody?> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TestBody>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// Issues a session + CSRF cookie pair (the create POST requires an allowed
    /// Origin) and returns the raw Set-Cookie headers for both cookies.
    /// </summary>
    private static async Task<(string SessionCookie, string CsrfCookie)> CreateSessionAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
        request.Headers.Add("Origin", "https://panel.local");
        var create = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var sessionCookie = create.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"{CookieName}=", StringComparison.Ordinal));
        var csrfCookie = create.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith($"{CsrfCookieName}=", StringComparison.Ordinal));
        return (sessionCookie, csrfCookie);
    }

    private static string ExtractCsrfToken(string setCookieHeader)
    {
        var start = setCookieHeader.IndexOf('=') + 1;
        var end = setCookieHeader.IndexOf(';', start);
        return end < 0 ? setCookieHeader[start..] : setCookieHeader[start..end];
    }

    /// <summary>Reads every text column of every table in the test database.</summary>
    private static string ReadAllDatabaseText(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();

        var text = new System.Text.StringBuilder();
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = tables.ExecuteReader();
            var tableNames = new List<string>();
            while (reader.Read())
            {
                tableNames.Add(reader.GetString(0));
            }

            foreach (var table in tableNames)
            {
                using var content = connection.CreateCommand();
                content.CommandText = $"SELECT * FROM [{table}];";
                using var contentReader = content.ExecuteReader();
                while (contentReader.Read())
                {
                    for (var i = 0; i < contentReader.FieldCount; i++)
                    {
                        text.Append(contentReader.GetValue(i)?.ToString()).Append('|');
                    }

                    text.AppendLine();
                }
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task Create_IssuesCookieWithSecurityFlags()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
            request.Headers.Add("Origin", "https://panel.local");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var setCookie = response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith($"{CookieName}=", StringComparison.Ordinal));
            Assert.StartsWith($"{CookieName}=", setCookie);
            // ASP.NET Core emits lowercase cookie attribute names.
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

            // The raw id is carried by the cookie only — never in the body (T11).
            var body = await response.Content.ReadAsStringAsync();
            var cookieValue = setCookie[$"{CookieName}=".Length..setCookie.IndexOf(';')];
            Assert.DoesNotContain(cookieValue, body);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Current_WithValidSession_Returns200()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var createResponse = await CreateSessionAsync(client);
            var setCookie = createResponse.SessionCookie;
            var rawId = setCookie[$"{CookieName}=".Length..setCookie.IndexOf(';')];
            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={rawId}; {createResponse.CsrfCookie}");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", ExtractCsrfToken(createResponse.CsrfCookie));

            var response = await client.GetAsync("/api/test/sessions/current");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Current_WithoutCookie_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.GetAsync("/api/test/sessions/current");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Current_WithUnknownSessionId_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={new string('A', 64)}; {CsrfCookieName}=token");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", "token");

            var response = await client.GetAsync("/api/test/sessions/current");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    /// <summary>T8: absolute expiration is enforced server-side; the expired id is rejected.</summary>
    [Fact]
    public async Task Current_WithExpiredSession_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var store = factory.Services.GetRequiredService<ISessionStore>();
            var rawId = "expired-session-raw-id-for-test-0001";
            await store.InsertAsync(new SessionRecord
            {
                IdHash = SessionService.HashId(rawId),
                AdminId = "admin",
                CreatedUtc = DateTime.UtcNow.AddHours(-13),
                ExpiresUtc = DateTime.UtcNow.AddHours(-1),
                IdleExpiresUtc = null,
                Revoked = false,
            });
            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={rawId}; {CsrfCookieName}=token");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", "token");

            var response = await client.GetAsync("/api/test/sessions/current");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    /// <summary>T9: invalidation revokes the session; the revoked id is rejected immediately
    /// and the response clears the cookie.</summary>
    [Fact]
    public async Task Invalidate_RevokesSessionImmediately_AndClearsCookie()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var createResponse = await CreateSessionAsync(client);
            var setCookie = createResponse.SessionCookie;
            var rawId = setCookie[$"{CookieName}=".Length..setCookie.IndexOf(';')];
            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={rawId}; {createResponse.CsrfCookie}");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", ExtractCsrfToken(createResponse.CsrfCookie));
            client.DefaultRequestHeaders.Add("Origin", "https://panel.local");

            var invalidateResponse = await client.PostAsync("/api/test/sessions/invalidate", content: null);
            Assert.Equal(HttpStatusCode.OK, invalidateResponse.StatusCode);

            // The response carries an expired cookie so the browser drops it.
            var clearCookie = invalidateResponse.Headers
                .FirstOrDefault(h => h.Key == "Set-Cookie").Value
                ?.FirstOrDefault(v => v.StartsWith($"{CookieName}="));
            Assert.NotNull(clearCookie);
            Assert.Contains("expires=", clearCookie!, StringComparison.OrdinalIgnoreCase);

            client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={rawId}");
            var currentResponse = await client.GetAsync("/api/test/sessions/current");
            Assert.Equal(HttpStatusCode.Unauthorized, currentResponse.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    /// <summary>T10: expired and revoked rows are removed by the cleanup path.</summary>
    [Fact]
    public async Task PruneExpired_RemovesExpiredAndRevokedRows_KeepsValid()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var store = factory.Services.GetRequiredService<ISessionStore>();
            var expiredRawId = "prune-test-expired-raw-id-00000001";
            var revokedRawId = "prune-test-revoked-raw-id-00000001";
            var validRawId = "prune-test-valid-raw-id-0000000001";
            await store.InsertAsync(new SessionRecord
            {
                IdHash = SessionService.HashId(expiredRawId),
                AdminId = "admin",
                CreatedUtc = DateTime.UtcNow.AddHours(-13),
                ExpiresUtc = DateTime.UtcNow.AddHours(-1),
                IdleExpiresUtc = null,
                Revoked = false,
            });
            await store.InsertAsync(new SessionRecord
            {
                IdHash = SessionService.HashId(revokedRawId),
                AdminId = "admin",
                CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                ExpiresUtc = DateTime.UtcNow.AddHours(12),
                IdleExpiresUtc = null,
                Revoked = true,
            });
            await store.InsertAsync(new SessionRecord
            {
                IdHash = SessionService.HashId(validRawId),
                AdminId = "admin",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(12),
                IdleExpiresUtc = null,
                Revoked = false,
            });

            var pruned = await store.PruneExpiredAsync(DateTime.UtcNow);

            Assert.Equal(2, pruned);
            Assert.Null(await store.GetByIdHashAsync(SessionService.HashId(expiredRawId)));
            Assert.Null(await store.GetByIdHashAsync(SessionService.HashId(revokedRawId)));
            Assert.NotNull(await store.GetByIdHashAsync(SessionService.HashId(validRawId)));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    /// <summary>T11: only the session-id hash is stored; the raw id never appears in the
    /// database or in any response body.</summary>
    [Fact]
    public async Task Create_StoresHashOnly_RawIdNeverPersistedOrReturned()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
            request.Headers.Add("Origin", "https://panel.local");
            var response = await client.SendAsync(request);
            var setCookie = response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith($"{CookieName}=", StringComparison.Ordinal));
            var rawId = setCookie[$"{CookieName}=".Length..setCookie.IndexOf(';')];
            var responseBody = await response.Content.ReadAsStringAsync();

            var databaseText = ReadAllDatabaseText(databasePath);

            Assert.DoesNotContain(rawId, databaseText);
            Assert.DoesNotContain(rawId, responseBody);
            Assert.Contains(SessionService.HashId(rawId), databaseText);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    /// <summary>Endpoints are double-gated: without the configuration flag they do not
    /// exist (404), matching the forced-error gate pattern.</summary>
    [Fact]
    public async Task TestSessionEndpoints_WhenDisabled_Return404()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "false");
            builder.UseSetting("Database:Path", System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hostingadmin-tests", $"sessions-{Guid.NewGuid():N}.db"));
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
        });
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
        request.Headers.Add("Origin", "https://panel.local");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
