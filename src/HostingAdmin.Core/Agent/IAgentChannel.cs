namespace HostingAdmin.Core.Agent;

/// <summary>
/// Outcome of an agent operation, as reported by the agent (plan §3.3).
/// </summary>
public enum AgentOperationOutcome
{
    Success,
    Failure,
    Rejected,
}

/// <summary>
/// Result of a call through the agent channel. The API maps these to
/// structured responses without exposing agent internals (plan §4).
/// </summary>
public sealed record AgentChannelResult(
    AgentOperationOutcome Outcome,
    string Detail,
    string? RequestId = null)
{
    /// <summary>Structured "unavailable" result used when the agent cannot be reached (D24).</summary>
    public static AgentChannelResult Unavailable { get; } =
        new(AgentOperationOutcome.Failure, "The privileged agent is unavailable.");
}

/// <summary>
/// The single trust boundary between the API and privileged operations (D16,
/// plan §3.1): the API never spawns systemctl for mutations and never holds
/// sudo rights — every mutation crosses this channel.
/// </summary>
public interface IAgentChannel
{
    /// <summary>
    /// Whether the agent channel is expected to work in the current
    /// environment. False on Windows dev (D17) — the API reports
    /// <c>mutationCapabilityAvailable: false</c> and the frontend disables
    /// mutation controls (D24).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sends a service action request to the agent. Task 3 implements the
    /// real unix-socket channel; task 1 only defines the contract and the
    /// null channel.
    /// </summary>
    Task<AgentChannelResult> SendServiceActionAsync(
        string unit, string action, CancellationToken cancellationToken);
}
