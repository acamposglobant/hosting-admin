using System.Net;
using System.Text;
using System.Text.Json;
using HostingAdmin.Core.Agent;
using HostingAdmin.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for the service mutation endpoints (plan task 4, §4):
/// session + CSRF required (TA7), API-side allowlist enforcement with no
/// channel call for non-allowlisted units (TA8), confirmation flow semantics
/// (TA9), and capability unavailability (TA10).
/// </summary>
public sealed class ServiceMutationEndpointTests
{
    private const string SessionCookieName = "ha_session";
    private const string CsrfCookieName = "ha_csrf";
    private const string CsrfHeaderName = "X-CSRF-Token";

    /// <summary>Channel double that counts mutation calls (TA8 assertion).</summary>
    public sealed class CountingAgentChannel : IAgentChannel
    {
        public bool Available { get; set; } = true;

        public int MutationCallCount { get; private set; }

        public bool IsAvailable => Available;

        public Task<AgentChannelResult> SendServiceActionAsync(
            string unit, string action, CancellationToken cancellationToken)
        {
            MutationCallCount++;
            return Task.FromResult(new AgentChannelResult(AgentOperationOutcome.Success, "ok"));
        }

        public Task<AgentChannelResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentChannelResult(AgentOperationOutcome.Success, "probe"));
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client, string DatabasePath, CountingAgentChannel Channel) CreateHost(
        CountingAgentChannel? channel = null)
    {
        var agentChannel = channel ?? new CountingAgentChannel();
        var databasePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hostingadmin-tests",
            $"mutations-{Guid.NewGuid():N}.db");
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
                services.RemoveAll<IAgentChannel>();
                services.AddSingleton<IAgentChannel>(agentChannel);
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        return (factory, client, databasePath, agentChannel);
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

    private static HttpRequestMessage Post(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
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
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {CsrfCookieName}={csrfToken}");
        client.DefaultRequestHeaders.Add(CsrfHeaderName, csrfToken);
    }

    // --- TA7: auth required ---

    [Fact]
    public async Task Mutation_WithoutSession_Returns401()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            foreach (var url in new[]
                     {
                         "/api/services/nginx.service/start",
                         "/api/services/nginx.service/stop",
                         "/api/services/nginx.service/restart",
                         "/api/service-actions/whatever/confirm",
                         "/api/service-actions/whatever/cancel",
                     })
            {
                var response = await client.SendAsync(Post(url));
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // --- TA8: non-allowlisted unit never reaches the channel ---

    [Fact]
    public async Task Mutation_NonAllowlistedUnit_Returns404_and_channel_never_called()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            foreach (var action in new[] { "start", "stop", "restart" })
            {
                var response = await client.SendAsync(Post($"/api/services/evil.service/{action}"));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            Assert.Equal(0, channel.MutationCallCount);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // --- start: direct execution ---

    [Fact]
    public async Task Start_WithSessionAndCsrf_executes_directly()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Post("/api/services/nginx.service/start"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, channel.MutationCallCount);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("nginx.service", document.RootElement.GetProperty("unit").GetString());
            Assert.Equal("start", document.RootElement.GetProperty("action").GetString());
            Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // --- stop/restart: two-step confirmation (D20) ---

    [Fact]
    public async Task Stop_returns_confirmation_and_does_not_execute()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Post("/api/services/nginx.service/stop"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, channel.MutationCallCount); // no execution yet

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("confirmationId", out var confirmationId));
            Assert.Equal("nginx.service", root.GetProperty("unit").GetString());
            Assert.Equal("stop", root.GetProperty("action").GetString());
            Assert.True(root.TryGetProperty("expiresAt", out _));
            Assert.False(string.IsNullOrEmpty(confirmationId.GetString()));
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Restart_returns_confirmation()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Post("/api/services/nginx.service/restart"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, channel.MutationCallCount);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Confirm_executes_then_second_confirm_fails_single_use()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var pending = await CreateConfirmationAsync(client, "/api/services/nginx.service/stop");

            var confirm = await client.SendAsync(Post($"/api/service-actions/{pending}/confirm"));
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            Assert.Equal(1, channel.MutationCallCount);

            var body = await confirm.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("nginx.service", document.RootElement.GetProperty("unit").GetString());
            Assert.Equal("stop", document.RootElement.GetProperty("action").GetString());
            Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());

            // Single-use: the second confirm is rejected (TA9).
            var second = await client.SendAsync(Post($"/api/service-actions/{pending}/confirm"));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(1, channel.MutationCallCount);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Cancel_discards_confirmation_then_confirm_fails()
    {
        var (factory, client, databasePath, channel) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var pending = await CreateConfirmationAsync(client, "/api/services/nginx.service/restart");

            var cancel = await client.SendAsync(Post($"/api/service-actions/{pending}/cancel"));
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal(0, channel.MutationCallCount);

            var confirm = await client.SendAsync(Post($"/api/service-actions/{pending}/confirm"));
            Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    [Fact]
    public async Task Confirm_with_unknown_token_returns409()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Post("/api/service-actions/bogus/confirm"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // --- TA10: agent unavailable ---

    [Fact]
    public async Task Start_with_unavailable_agent_returns503()
    {
        var (factory, client, databasePath, _) = CreateHost(new CountingAgentChannel { Available = false });
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var response = await client.SendAsync(Post("/api/services/nginx.service/start"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("capability_unavailable", body);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    // --- D25: confirmation token never appears in audit rows ---

    [Fact]
    public async Task Confirmation_token_never_appears_in_audit_rows()
    {
        var (factory, client, databasePath, _) = CreateHost();
        try
        {
            var (sessionCookie, csrfToken) = await CreateSessionAsync(client);
            ApplyAuth(client, sessionCookie, csrfToken);

            var pending = await CreateConfirmationAsync(client, "/api/services/nginx.service/stop");
            await client.SendAsync(Post($"/api/service-actions/{pending}/confirm"));

            var auditResponse = await client.SendAsync(Get("/api/audit?take=200"));
            Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            var auditBody = await auditResponse.Content.ReadAsStringAsync();

            Assert.DoesNotContain(pending, auditBody, StringComparison.Ordinal);
        }
        finally
        {
            DisposeHost(factory, client, databasePath);
        }
    }

    private static HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://panel.local");
        return request;
    }

    private static async Task<string> CreateConfirmationAsync(HttpClient client, string url)
    {
        var response = await client.SendAsync(Post(url));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("confirmationId").GetString()!;
    }
}
