namespace HostingAdmin.Core.Services;

/// <summary>
/// A pending destructive service action (stop/restart) awaiting confirmation
/// (plan §3.4, D20). The raw token is returned to the client exactly once at
/// creation; only its SHA-256 hash is persisted (same posture as session ids).
/// </summary>
public sealed record ServiceActionConfirmation(
    /// <summary>SHA-256 hash of the opaque confirmation token (primary key).</summary>
    string IdHash,
    /// <summary>Allowlisted unit (exact match at creation).</summary>
    string Unit,
    /// <summary>'stop' or 'restart'.</summary>
    string Action,
    /// <summary>Creating session id hash (binding).</summary>
    string SessionHash,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    bool Consumed);
