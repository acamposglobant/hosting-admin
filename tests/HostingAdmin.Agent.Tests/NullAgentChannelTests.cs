using HostingAdmin.Core.Agent;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Null channel behavior (D17, D24): on Windows dev every mutation reports a
/// structured "unavailable" result and the channel is never "available".
/// </summary>
public class NullAgentChannelTests
{
    [Fact]
    public void Channel_is_never_available()
    {
        var channel = new NullAgentChannel();
        Assert.False(channel.IsAvailable);
    }

    [Fact]
    public async Task SendServiceAction_returns_structured_unavailable_result()
    {
        var channel = new NullAgentChannel();
        var result = await channel.SendServiceActionAsync("nginx.service", "restart", CancellationToken.None);
        Assert.Equal(AgentOperationOutcome.Failure, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }
}
