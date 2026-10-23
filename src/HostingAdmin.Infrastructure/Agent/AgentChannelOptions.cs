namespace HostingAdmin.Infrastructure.Agent;

/// <summary>
/// Configuration for the API→agent channel (plan task 3, D22). Validated at
/// startup (fail-fast, D22): the API refuses to start with an invalid agent
/// channel configuration rather than degrading at request time.
/// </summary>
public sealed class AgentChannelOptions
{
    public const string SectionName = "AgentChannel";

    /// <summary>
    /// Path of the agent's unix domain socket. Must be absolute (D17: the
    /// production path is /run/hostingadmin/agent.sock).
    /// </summary>
    public string SocketPath { get; init; } = "/run/hostingadmin/agent.sock";

    /// <summary>
    /// Path of the mode-600 shared secret file shared with the agent. Read at
    /// startup; the secret is held in memory only and never logged (D25).
    /// </summary>
    public string SecretFile { get; init; } = "/etc/hostingadmin/agent-shared-secret";

    /// <summary>Per-call timeout for agent requests (D22).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for the health probe used to compute
    /// <c>mutationCapabilityAvailable</c>. Shorter than the action timeout so
    /// service-status polling stays fast when the agent is down.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(750);
}
