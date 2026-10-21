namespace HostingAdmin.Core.Audit;

/// <summary>Audit outcome values (plan §4 schema: 'Success' | 'Failure').</summary>
public static class AuditOutcomes
{
    public const string Success = "Success";
    public const string Failure = "Failure";
}

/// <summary>
/// A single audit record. Audit rows are append-only: there are no update or
/// delete paths for individual events (retention prune is the only deletion).
/// </summary>
public sealed record AuditEvent
{
    /// <summary>UTC timestamp of the event.</summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>Who performed the action (e.g. 'system', 'admin', 'anonymous').</summary>
    public required string Actor { get; init; }

    /// <summary>What was done (e.g. 'auth.login', 'auth.login_failed', 'audit.query').</summary>
    public required string Action { get; init; }

    /// <summary>'Success' or 'Failure'.</summary>
    public required string Outcome { get; init; }

    /// <summary>Remote IP of the request, when applicable.</summary>
    public string? SourceIp { get; init; }

    /// <summary>
    /// Free-form detail. Scrubbed of secret material before persistence (D7);
    /// never contains passwords, session identifiers, or credential material.
    /// </summary>
    public string? Detail { get; init; }
}
