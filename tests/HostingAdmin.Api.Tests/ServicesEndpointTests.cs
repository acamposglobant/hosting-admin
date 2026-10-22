using System.Net;
using System.Text.Json;
using HostingAdmin.Core.Services;
using HostingAdmin.Infrastructure.Linux;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for GET /api/services (plan task 12): session + CSRF
/// required, no client parameters (D12), structured "unavailable" shape on
/// Windows (D11), contract shape, and no audit rows per poll (D8).
/// </summary>
public sealed class ServicesEndpointTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";

    private sealed class FakeServiceStatusProvider : IServiceStatusProvider
    {
        public Task<ServicesSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServicesSnapshot(
                CapabilityAvailable: true,
                UnavailableReason: null,
                Services:
                [
                    new ServiceStatus("nginx.service", "loaded", "active", "running", "enabled",
                        DateTimeOffset.Parse("2026-10-20T08:14:03Z")),
                    new ServiceStatus("notinstalled.service", "not-found", "inactive", "dead", "unknown", null),
                ]));
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost(
        IServiceStatusProvider? serviceStatusProvider = null)
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"services-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
            builder.UseSetting("Services:UnitNames:0", "nginx.service");
            builder.UseSetting("Services:UnitNames:1", "ssh.service");
            builder.ConfigureServices(services =>
            {
                if (serviceStatusProvider is not null)
                {
                    services.RemoveAll<IServiceStatusProvider>();
                    services.AddSingleton(serviceStatusProvider);
                }
            });
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

    [Fact]
    public async Task Services_WithoutSession_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Get("/api/services"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Services_WithSessionAndCsrf_ReturnsContractShape()
    {
        var (factory, client, databasePath) = CreateHost(new FakeServiceStatusProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/services"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("capabilityAvailable", out var capability));
            Assert.True(capability.GetBoolean());
            Assert.True(root.TryGetProperty("services", out var services));
            Assert.Equal(JsonValueKind.Array, services.ValueKind);

            var first = services.EnumerateArray().First();
            Assert.True(first.TryGetProperty("name", out _));
            Assert.True(first.TryGetProperty("loadState", out _));
            Assert.True(first.TryGetProperty("activeState", out _));
            Assert.True(first.TryGetProperty("subState", out _));
            Assert.True(first.TryGetProperty("enabledState", out _));
            Assert.True(first.TryGetProperty("since", out _));

            // A not-found unit is reported, not hidden (plan §4).
            var notFound = services.EnumerateArray()
                .Single(service => service.GetProperty("name").GetString() == "notinstalled.service");
            Assert.Equal("not-found", notFound.GetProperty("loadState").GetString());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Services_UnavailableSource_ReturnsStructuredUnavailableShape()
    {
        var (factory, client, databasePath) = CreateHost(new NullServiceStatusProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/services"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("capabilityAvailable", out var capability));
            Assert.False(capability.GetBoolean());
            Assert.True(root.TryGetProperty("unavailableReason", out _));
            Assert.True(root.TryGetProperty("services", out var services));
            Assert.Equal(JsonValueKind.Array, services.ValueKind);
            Assert.False(services.EnumerateArray().Any());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Services_PollsAreNotAudited()
    {
        var (factory, client, databasePath) = CreateHost(new FakeServiceStatusProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            await client.SendAsync(Get("/api/services"));
            await client.SendAsync(Get("/api/services"));
            await client.SendAsync(Get("/api/services"));

            Assert.Equal(0, await CountAuditRowsAsync(factory));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
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
}
