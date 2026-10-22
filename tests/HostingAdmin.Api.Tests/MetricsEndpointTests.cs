using System.Net;
using System.Text.Json;
using HostingAdmin.Core.Metrics;
using HostingAdmin.Infrastructure.Linux;
using HostingAdmin.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for GET /api/metrics (plan task 10): session + CSRF
/// required, bounded cache/single-flight under bursts (D10), structured
/// "unavailable" shape when no source exists (D11), and no audit row per
/// poll (D8).
/// </summary>
public sealed class MetricsEndpointTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";

    /// <summary>Counting provider used to assert bounded collections under bursts.</summary>
    private sealed class CountingMetricsProvider : IMetricsProvider
    {
        private int invocationCount;

        public int InvocationCount => invocationCount;

        public Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(new MetricsSnapshot(
                IsAvailable: true,
                UnavailableReason: null,
                Cpu: new CpuMetrics(UsagePercent: 12.5, Cores: 2, LoadAverage1m: 0.3, LoadAverage5m: 0.2,
                    LoadAverage15m: 0.1, SampledAt: DateTime.UtcNow),
                Memory: new MemoryMetrics(TotalBytes: 2048, UsedBytes: 1024, AvailableBytes: 1024, UsagePercent: 50.0),
                Swap: new SwapMetrics(TotalBytes: 512, UsedBytes: 0, UsagePercent: 0.0),
                UptimeSeconds: 864000));
        }
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath, IMetricsProvider Provider) CreateHost(IMetricsProvider? providerOverride = null)
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"metrics-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var provider = providerOverride ?? new CountingMetricsProvider();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Diagnostics:TestSessionsEnabled", "true");
            builder.UseSetting("Metrics:CacheTtl", "00:00:05");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMetricsProvider>();
                services.AddSingleton<IMetricsProvider>(provider);
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        return (factory, client, databasePath, provider);
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

    /// <summary>
    /// Creates a session via the test endpoint and returns the session cookie
    /// and CSRF token (from the CSRF cookie), mirroring the login flow.
    /// </summary>
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
    public async Task Metrics_WithoutSession_Returns401()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var response = await client.SendAsync(Get("/api/metrics"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_WithSessionButNoCsrf_Returns4xx()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var (sessionCookie, _) = await CreateSessionAsync(client);
            client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

            var response = await client.SendAsync(Get("/api/metrics"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_WithSessionAndCsrf_ReturnsContractShape()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/metrics"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("isAvailable", out var available));
            Assert.True(available.GetBoolean());
            Assert.True(root.TryGetProperty("cpu", out var cpu));
            Assert.True(cpu.TryGetProperty("usagePercent", out _));
            Assert.True(cpu.TryGetProperty("cores", out _));
            Assert.True(cpu.TryGetProperty("loadAverage1m", out _));
            Assert.True(cpu.TryGetProperty("sampledAt", out _));
            Assert.True(root.TryGetProperty("memory", out var memory));
            Assert.True(memory.TryGetProperty("totalBytes", out _));
            Assert.True(memory.TryGetProperty("usagePercent", out _));
            Assert.True(root.TryGetProperty("swap", out var swap));
            Assert.True(root.TryGetProperty("uptimeSeconds", out _));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_BurstRequests_PerformBoundedCollections()
    {
        var (factory, client, databasePath, provider) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var requests = Enumerable.Range(0, 10)
                .Select(_ => client.SendAsync(Get("/api/metrics")))
                .ToArray();
            var responses = await Task.WhenAll(requests);

            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            // D10: the burst collapses into at most two collections (single-flight
            // plus a possible post-cache re-check) instead of ten.
            var countingProvider = Assert.IsType<CountingMetricsProvider>(provider);
            Assert.True(countingProvider.InvocationCount <= 2,
                $"expected bounded collections, got {countingProvider.InvocationCount}");
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_PollsAreNotAudited()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            for (var index = 0; index < 3; index++)
            {
                var response = await client.SendAsync(Get("/api/metrics"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(0, await CountAuditRowsAsync(factory));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Metrics_UnavailableSource_ReturnsStructuredUnavailableShape()
    {
        // Windows dev / no provider source: the endpoint must return the
        // structured "unavailable" shape (D11), not an error.
        var (factory, client, databasePath, _) = CreateHost(new NullMetricsProvider());
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Get("/api/metrics"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.False(document.RootElement.GetProperty("isAvailable").GetBoolean());
            Assert.Equal("metrics_source_unavailable",
                document.RootElement.GetProperty("unavailableReason").GetString());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }
}
