using System.Text.Json;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Portable regression tests for the agent wire format (CI failure root
/// cause): the protocol is camelCase on the wire. These tests run on Windows
/// too, so a naming-policy regression can never again hide behind the
/// Linux-only socket tests.
/// </summary>
public class AgentWireFormatTests
{
    [Fact]
    public void AgentRequest_deserializes_camelCase_wire_document()
    {
        const string json = """{"requestId":"r-1","action":"restart","unit":"nginx.service"}""";

        var request = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentRequest);

        Assert.NotNull(request);
        Assert.Equal("r-1", request.RequestId);
        Assert.Equal("restart", request.Action);
        Assert.Equal("nginx.service", request.Unit);
    }

    [Fact]
    public void AgentRequest_PascalCase_document_is_not_accepted()
    {
        // The protocol is exact: PascalCase keys must NOT deserialize (this is
        // what silently broke the Linux CI run — every request became null).
        const string json = """{"RequestId":"r-1","Action":"start","Unit":"nginx.service"}""";

        var request = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentRequest);

        Assert.True(request is null || request.Action is null,
            "PascalCase document must not deserialize into a valid request");
    }

    [Fact]
    public void AgentResponse_serializes_to_camelCase()
    {
        var response = new AgentResponse(RequestId: "r-2", Outcome: "Success", Detail: "ok");

        var json = JsonSerializer.Serialize(response, AgentJsonContext.Default.AgentResponse);

        Assert.Contains("\"requestId\"", json);
        Assert.Contains("\"outcome\"", json);
        Assert.Contains("\"detail\"", json);
        Assert.DoesNotContain("\"RequestId\"", json);
    }
}
