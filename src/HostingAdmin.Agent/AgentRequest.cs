using System.Text.Json.Serialization;

namespace HostingAdmin.Agent;

/// <summary>
/// Request document (plan §3.3): one JSON document per connection, after the
/// secret line. The agent echoes <c>RequestId</c> for correlation but does
/// not interpret it.
/// </summary>
public sealed record AgentRequest(string? RequestId, string? Action, string? Unit);

/// <summary>Fixed set of service actions the agent will ever run (D23).</summary>
public static class AgentActions
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    public static bool IsDefined(string? action) =>
        action is Start or Stop or Restart;
}

/// <summary>
/// JSON source generation for the agent request protocol. Wire format is
/// camelCase; deserialization is exact (no case-insensitive fallback) so the
/// protocol is unambiguous.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentRequest))]
internal sealed partial class AgentRequestJson : JsonSerializerContext;
