namespace HostingAdmin.Agent;

/// <summary>
/// Agent configuration (plan D17, D22). Validated at startup — the agent
/// fails fast and refuses to run with missing or unsafe configuration.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Unix domain socket path the agent listens on (D17). Must be an absolute
    /// path; the agent never binds TCP.
    /// </summary>
    public string SocketPath { get; init; } = "/run/hostingadmin/agent.sock";

    /// <summary>
    /// Path to the shared secret file (D17, D22). The file must exist and be
    /// readable by the agent account; its contents are required on every request.
    /// </summary>
    public string SecretFile { get; init; } = "/etc/hostingadmin/agent-secret";

    /// <summary>
    /// Maximum concurrent connections (D22). Small by design; excess
    /// connections are rejected cleanly.
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 2;

    /// <summary>
    /// Per-request timeout (D22). A single request must never hang the agent.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Maximum accepted request size in bytes (D22). Requests are tiny JSON
    /// documents; anything larger is rejected before parsing.
    /// </summary>
    public int MaxRequestBytes { get; init; } = 4096;
}
