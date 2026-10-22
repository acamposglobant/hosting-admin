using System.Net;
using System.Text.Json;
using HostingAdmin.Core.SystemInfo;
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
/// Integration tests for GET /api/system/info and GET /api/disks (plan task 11):
/// session + CSRF required, structured "unavailable" shape on Windows (D11),
/// contract shapes, and no audit rows per poll (D8).
/// </summary>
public sealed class SystemInfoEndpointTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";

    private sealed class FakeSystemInfoProvider : ISystemInfoProvider
    {
        public Task<SystemInfoSnapshot> GetInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SystemInfoSnapshot(
                IsAvailable: true,
                UnavailableReason: null,
                Hostname: "vps01",
                OperatingSystem: "Ubuntu 24.04.1 LTS",
                KernelVersion: "6.8.0-45-generic",
                Architecture: "X64"));
    }

    private sealed class FakeDiskProvider : IDiskProvider
    {
        public Task<DiskSnapshot> GetDisksAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DiskSnapshot(
                IsAvailable: true,
                UnavailableReason: null,
                new List<DiskUsage>
                {
                    new("/dev/sda1", "/", "ext4", 1000, 200, 800, 20.0),
                }));
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost(
        ISystemInfoProvider? systemInfoProvider = null,
        IDiskProvider? diskProvider = null)
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"sysinfo-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
            builder.ConfigureServices(services =>
            {
                if (systemInfoProvider is not null)
                {
                    services.RemoveAll<ISystemInfoProvider>();
                    services.AddSingleton(systemInfoProvider);
                }

                if (diskProvider is not null)
                {
                    services.RemoveAll<IDiskProvider>();
                    services.AddSingleton<IDiskProvider>(diskProvider);
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
        // The CSRF middleware validates the X-CSRF-Token header against the
        // csrf cookie, so both cookies must travel with the request.
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {CsrfCookieName}={csrfToken}");
        client.DefaultRequestHeaders.Add(CsrfHeaderName, csrfToken);
    }

    [Fact]
    public async Task SystemInfo_WithoutSession_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Get("/api/system/info"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Disks_WithoutSession_Returns401()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Get("/api/disks"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task SystemInfo_WithSessionAndCsrf_ReturnsContractShape()
    {
        var (factory, client, databasePath) = CreateHost(new FakeSystemInfoProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/system/info"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("isAvailable", out var available));
            Assert.True(available.GetBoolean());
            Assert.True(root.TryGetProperty("hostname", out var hostname));
            Assert.Equal("vps01", hostname.GetString());
            Assert.True(root.TryGetProperty("operatingSystem", out _));
            Assert.True(root.TryGetProperty("kernelVersion", out _));
            Assert.True(root.TryGetProperty("architecture", out _));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Disks_WithSessionAndCsrf_ReturnsContractShape()
    {
        var (factory, client, databasePath) = CreateHost(diskProvider: new FakeDiskProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/disks"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("isAvailable", out var available));
            Assert.True(available.GetBoolean());
            Assert.True(root.TryGetProperty("disks", out var disks));
            Assert.Equal(JsonValueKind.Array, disks.ValueKind);
            var firstDisk = disks.EnumerateArray().First();
            Assert.True(firstDisk.TryGetProperty("device", out _));
            Assert.True(firstDisk.TryGetProperty("mountPoint", out _));
            Assert.True(firstDisk.TryGetProperty("fileSystem", out _));
            Assert.True(firstDisk.TryGetProperty("totalBytes", out _));
            Assert.True(firstDisk.TryGetProperty("usedBytes", out _));
            Assert.True(firstDisk.TryGetProperty("availableBytes", out _));
            Assert.True(firstDisk.TryGetProperty("usagePercent", out _));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task SystemInfo_UnavailableSource_ReturnsStructuredUnavailableShape()
    {
        // Windows dev / no provider source: the endpoint must return the
        // structured "unavailable" shape (D11), not an error.
        var (factory, client, databasePath) = CreateHost(new NullSystemInfoProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/system/info"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("isAvailable", out var available));
            Assert.False(available.GetBoolean());
            Assert.True(root.TryGetProperty("unavailableReason", out _));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Disks_UnavailableSource_ReturnsStructuredUnavailableShape()
    {
        var (factory, client, databasePath) = CreateHost(diskProvider: new NullDiskProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/disks"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("isAvailable", out var available));
            Assert.False(available.GetBoolean());
            Assert.True(root.TryGetProperty("unavailableReason", out _));
            Assert.True(root.TryGetProperty("disks", out var disks));
            Assert.Equal(JsonValueKind.Array, disks.ValueKind);
            Assert.False(disks.EnumerateArray().Any());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task SystemInfoAndDisks_PollsAreNotAudited()
    {
        var (factory, client, databasePath) = CreateHost(new FakeSystemInfoProvider(), new FakeDiskProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            await client.SendAsync(Get("/api/system/info"));
            await client.SendAsync(Get("/api/disks"));
            await client.SendAsync(Get("/api/system/info"));

            // Selective audit-on-access (task 13): first data access per session
            // writes exactly one row (shared across data endpoints); repeated
            // polls add nothing (T15).
            Assert.Equal(1, await CountAuditRowsAsync(factory));
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
