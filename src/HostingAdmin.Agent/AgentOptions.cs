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

    /// <summary>
    /// Allowlisted systemd units (D18, D23). Exact-match only; validated at
    /// startup. The agent re-validates every request against this list and
    /// never trusts API-side validation.
    /// </summary>
    public string[] UnitNames { get; init; } = [];

    /// <summary>
    /// Absolute path to the systemctl binary (D19). Fixed at configuration
    /// time; the agent never resolves it from request input.
    /// </summary>
    public string SystemctlPath { get; init; } = "/usr/bin/systemctl";

    /// <summary>
    /// Hard timeout for the child systemctl process in seconds (D19). On
    /// expiry the child (and its tree) is killed and a structured failure is
    /// returned.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 10;
}
