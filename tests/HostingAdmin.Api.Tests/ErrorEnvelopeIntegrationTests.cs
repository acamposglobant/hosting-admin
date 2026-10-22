using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HostingAdmin.Api.Tests;

/// <summary>
/// Integration tests for the global error envelope (plan task 2):
/// forced exception → 500 envelope with no internals; unmatched route → 404 envelope.
/// </summary>
public class ErrorEnvelopeIntegrationTests
{
    private static HttpClient CreateClient(bool forceErrorEnabled)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Diagnostics:ForceErrorEnabled", forceErrorEnabled ? "true" : "false");
        });
        return factory.CreateClient();
    }

    private sealed record ErrorBody(string Code, string Message, JsonElement? Details, string TraceId);
    private sealed record Envelope(ErrorBody Error);

    private static async Task<Envelope?> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Envelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task ForceError_WhenDisabled_Returns404Envelope()
    {
        using var client = CreateClient(forceErrorEnabled: false);

        var response = await client.GetAsync("/api/diagnostics/force-error");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await DeserializeAsync(response);
        Assert.NotNull(envelope);
        Assert.Equal("not_found", envelope!.Error.Code);
    }

    [Fact]
    public async Task ForceError_WhenEnabled_Returns500Envelope()
    {
        using var client = CreateClient(forceErrorEnabled: true);

        var response = await client.GetAsync("/api/diagnostics/force-error");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var envelope = await DeserializeAsync(response);
        Assert.NotNull(envelope);
        Assert.Equal("internal_error", envelope!.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.TraceId));
    }

    [Fact]
    public async Task ForceError_ResponseContainsNoInternalDetails()
    {
        using var client = CreateClient(forceErrorEnabled: true);

        var response = await client.GetAsync("/api/diagnostics/force-error");
        var body = await response.Content.ReadAsStringAsync();

        // No exception type, message, stack trace, or paths in the body.
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("Forced test exception", body);
        Assert.DoesNotContain("at HostingAdmin", body);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", body);
    }

    [Fact]
    public async Task UnknownRoute_Returns404Envelope()
    {
        using var client = CreateClient(forceErrorEnabled: false);

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await DeserializeAsync(response);
        Assert.NotNull(envelope);
        Assert.Equal("not_found", envelope!.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.TraceId));
    }

    [Fact]
    public async Task HealthEndpoint_StillReturns200()
    {
        using var client = CreateClient(forceErrorEnabled: false);

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"healthy\"", body);
    }

    // --- Regression test (security review F4): force-error endpoint must be
    // unreachable in Production even if the configuration flag is accidentally set. ---

    private static HttpClient CreateProductionClient(bool forceErrorEnabled)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Diagnostics:ForceErrorEnabled", forceErrorEnabled ? "true" : "false");
            builder.UseSetting("environment", "Production");
            // This test exercises error mapping in the Production environment, not
            // proxy attestation (task 9); disable it so requests reach the endpoint.
            builder.UseSetting("Proxy:RequireAttestation", "false");
        });
        return factory.CreateClient();
    }

    [Fact]
    public async Task ForceError_InProductionEnvironment_IsNotMapped()
    {
        using var client = CreateProductionClient(forceErrorEnabled: true);

        var response = await client.GetAsync("/api/diagnostics/force-error");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
