using System.Net;
using System.Text;
using System.Text.Json;
using HostingAdmin.Core.Bootstrap;
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
/// Integration tests for first-admin bootstrap (plan §3.4, D6, task 8, T7):
/// disabled by default, local-only activation, limited window, persisted
/// completion. The remote address is controlled per request via the
/// X-Test-Remote-Ip header, which a test-only middleware maps onto
/// Connection.RemoteIpAddress — production code never reads that header.
/// </summary>
public sealed class BootstrapIntegrationTests
{
    private const string Password = "bootstrap-test-password-123";

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

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath) CreateHost(
        bool localClient = true)
    {
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"bootstrap-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.UseSetting("Bootstrap:WindowDuration", "00:15:00");
            builder.ConfigureServices(services =>
            {
                services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        // TestServer leaves Connection.RemoteIpAddress null; make the simulated
        // source address explicit for both local and non-local scenarios.
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", localClient ? "127.0.0.1" : "203.0.113.50");

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
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = GetDatabasePath(factory) }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = $action;";
        command.Parameters.AddWithValue("$action", action);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Overwrites the stored window expiry (used to simulate expiry deterministically).</summary>
    private static async Task SetWindowExpiryAsync(string databasePath, DateTime expiresUtc)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admin_state SET value = $value WHERE key = 'bootstrap.window_expires_utc';";
        command.Parameters.AddWithValue("$value", expiresUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Activate_FromNonLocal_Returns403_DoesNotOpenWindow_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost(localClient: false);
        try
        {
            var response = await client.SendAsync(Post("/api/bootstrap/activate"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.NotConfigured, state.Status);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.activate_denied_nonlocal"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Activate_FromLocal_OpensWindow_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Post("/api/bootstrap/activate"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("window_open", body);
            Assert.Contains("expiresUtc", body);

            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.WindowOpen, state.Status);
            Assert.NotNull(state.WindowExpiresUtc);
            Assert.True(state.WindowExpiresUtc!.Value > DateTime.UtcNow);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.activated"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Activate_WithForwardedHeaderSpoof_FromNonLocal_Returns403()
    {
        var (factory, client, databasePath) = CreateHost(localClient: false);
        try
        {
            var request = Post("/api/bootstrap/activate");
            // D6: forwarded headers must never grant local access.
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            request.Headers.Add("X-Original-For", "127.0.0.1");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.NotConfigured, state.Status);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Activate_WhileWindowOpen_KeepsExistingExpiry()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var first = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstBody = await first.Content.ReadAsStringAsync();
            var firstExpiry = JsonSerializer.Deserialize<JsonElement>(firstBody).GetProperty("expiresUtc").GetString();

            var second = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondBody = await second.Content.ReadAsStringAsync();
            var secondExpiry = JsonSerializer.Deserialize<JsonElement>(secondBody).GetProperty("expiresUtc").GetString();

            Assert.Equal(firstExpiry, secondExpiry);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_FromNonLocal_WithOpenWindow_Returns403_AndAudits()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

            // The admin-creation request comes from a non-local address.
            var remoteRequest = Post("/api/bootstrap/admin");
            remoteRequest.Headers.Add("X-Test-Remote-Ip", "203.0.113.50");
            remoteRequest.Content = JsonBody(new { password = Password });
            var response = await client.SendAsync(remoteRequest);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_denied_nonlocal"));

            // The window is still open for the real local operator.
            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.WindowOpen, state.Status);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_WithoutActivation_Returns403()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var response = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_denied_window_closed"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_WithExpiredWindow_Returns403()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

            await SetWindowExpiryAsync(databasePath, DateTime.UtcNow.AddMinutes(-1));

            var response = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_denied_window_closed"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_WeakPassword_Returns400_WindowStaysOpen_RetrySucceeds()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

            var weak = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = "short" })));
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
            var body = await weak.Content.ReadAsStringAsync();
            Assert.Contains("validation_failed", body);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_rejected_weak_password"));

            // The window stays open: the local operator can retry.
            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.WindowOpen, state.Status);

            var retry = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_Success_CreatesCredential_PersistsCompletion_AndAllowsLogin()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

            var create = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_created"));

            // Completion persisted; the window is closed.
            var state = await GetStateAsync(factory);
            Assert.Equal(BootstrapStatus.Completed, state.Status);
            Assert.Null(state.WindowExpiresUtc);

            // The credential works: login succeeds with the new password.
            var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonBody(new { password = Password }),
            };
            loginRequest.Headers.Add("Origin", "https://panel.local");
            var login = await client.SendAsync(loginRequest);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task AfterCompletion_Activate_And_CreateAdmin_Return403_AndAudit()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            var create = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);

            var activateAgain = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.Forbidden, activateAgain.StatusCode);

            var createAgain = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.Forbidden, createAgain.StatusCode);

            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.activate_denied_completed"));
            Assert.Equal(1, await CountAuditRowsAsync(factory, "bootstrap.admin_denied_completed"));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Completion_Persists_AcrossRestart()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            var create = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }
        finally
        {
            client.Dispose();
            factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // A fresh host on the same database: bootstrap stays completed (T7).
        var restartedFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Path", databasePath);
            builder.UseSetting("Security:AllowedOrigins:0", "https://panel.local");
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>());
        });
        var restartedClient = restartedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        try
        {
            var activate = await restartedClient.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.Forbidden, activate.StatusCode);

            var create = await restartedClient.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = Password })));
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }
        finally
        {
            DisposeHost(restartedFactory, restartedClient, databasePath);
        }
    }

    [Fact]
    public async Task CreateAdmin_WithMissingPassword_Returns400()
    {
        var (factory, client, databasePath) = CreateHost();
        try
        {
            var activate = await client.SendAsync(Post("/api/bootstrap/activate"));
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

            var response = await client.SendAsync(Post("/api/bootstrap/admin", JsonBody(new { password = "" })));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("validation_failed", body);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    private static async Task<BootstrapState> GetStateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBootstrapStateStore>();
        return await store.GetStateAsync();
    }
}
