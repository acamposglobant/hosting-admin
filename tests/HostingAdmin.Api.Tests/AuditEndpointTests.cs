using System.Net;
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
/// Integration tests for GET /api/audit (plan task 13): paged, take capped at
/// 200, session + CSRF, every audit-log view itself audited, and first data
/// access per session audited once with subsequent polls silent (T15).
/// </summary>
public sealed class AuditEndpointTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost()
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"audit-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

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

    private static HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://panel.local");
        return request;
    }

    private static async Task<(string SessionCookie, string CsrfToken)> CreateSessionAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/sessions");
        request.Headers.Add("Origin", "https://panel.local");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies), "expected cookies");
        var cookieList = cookies!.ToList();
        var sessionCookie = cookieList
            .First(value => value.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
        var csrfCookie = cookieList
            .First(value => value.StartsWith(CsrfCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
        var csrfToken = csrfCookie[(CsrfCookieName.Length + 1)..];
        return (sessionCookie, csrfToken);
    }

    private static void ApplyAuth(HttpClient client, string sessionCookie, string csrfToken)
    {
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {CsrfCookieName}={csrfToken}");
        client.DefaultRequestHeaders.Add(CsrfHeaderName, csrfToken);
    }

    private static string GetDatabasePath(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value.Path;

    private static async Task<int> CountAuditRowsAsync(WebApplicationFactory<Program> factory)
    {
        factory.Services.GetRequiredService<SqliteConnectionFactory>().EnsureInitialized();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    [Fact]
    public async Task Audit_WithoutSession_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Get("/api/audit"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Audit_WithSession_ReturnsPagedContractShape()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/audit?skip=0&take=10"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("items", out var items));
            Assert.Equal(JsonValueKind.Array, items.ValueKind);
            Assert.True(root.TryGetProperty("totalCount", out var totalCount));
            Assert.True(totalCount.TryGetInt32(out _));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Theory]
    [InlineData("take=0")]
    [InlineData("take=201")]
    [InlineData("take=-1")]
    [InlineData("skip=-1")]
    public async Task Audit_InvalidPaging_Returns400ValidationEnvelope(string badQuery)
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get($"/api/audit?{badQuery}"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("validation_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Audit_TakeAbove200_IsRejected_Take200_IsAccepted()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var tooLarge = await client.SendAsync(Get("/api/audit?take=201"));
            Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);

            var atCap = await client.SendAsync(Get("/api/audit?take=200"));
            Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Audit_EveryView_IsAudited()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            await client.SendAsync(Get("/api/audit?take=10"));
            await client.SendAsync(Get("/api/audit?take=10"));
            await client.SendAsync(Get("/api/audit?take=10"));

            // Each view writes exactly one audit.query row (plus the session
            // creation and first data access rows from setup).
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'audit.query';";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());

            Assert.Equal(3, count);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Audit_ViewResponse_IncludesTheJustWrittenAuditRow()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/audit?take=50"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("items");

            // The view itself is audited, so the newest row is the audit.query.
            Assert.True(items.GetArrayLength() >= 1);
            Assert.Equal("audit.query", items[0].GetProperty("action").GetString());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_FirstAccessAuditedOnce_SubsequentPollsSilent()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            await client.SendAsync(Get("/api/metrics"));
            await client.SendAsync(Get("/api/metrics"));
            await client.SendAsync(Get("/api/metrics"));

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'data.access';";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());

            Assert.Equal(1, count);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }
}
