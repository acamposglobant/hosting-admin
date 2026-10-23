namespace HostingAdmin.Core.Agent;

/// <summary>
/// Null channel used on Windows development (D17): the agent is not run there,
/// so every mutation reports a structured "unavailable" result (D24) instead
/// of attempting a connection. Never used in production.
/// </summary>
public sealed class NullAgentChannel : IAgentChannel
{
    public bool IsAvailable => false;

    public Task<AgentChannelResult> SendServiceActionAsync(
        string unit, string action, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentChannelResult.Unavailable);
    }

    public Task<AgentChannelResult> ProbeAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentChannelResult.Unavailable);
    }
}
