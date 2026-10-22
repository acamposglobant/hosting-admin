namespace HostingAdmin.Core.Bootstrap;

/// <summary>Persisted bootstrap lifecycle state (plan D6).</summary>
public enum BootstrapStatus
{
    /// <summary>No admin credential exists and no window was ever opened.</summary>
    NotConfigured,

    /// <summary>A local activation opened a limited initialization window.</summary>
    WindowOpen,

    /// <summary>The first admin was created; bootstrap is permanently closed.</summary>
    Completed,
}

/// <summary>
/// Bootstrap state snapshot. Carries no credential material (D7) — the password
/// hash lives only in <see cref="HostingAdmin.Core.Auth.IAdminCredentialStore"/>.
/// </summary>
public sealed record BootstrapState
{
    public BootstrapStatus Status { get; init; } = BootstrapStatus.NotConfigured;

    /// <summary>When the initialization window closes; non-null only while open.</summary>
    public DateTime? WindowExpiresUtc { get; init; }

    public static BootstrapState NotConfiguredState { get; } = new();

    public static BootstrapState OpenWindow(DateTime expiresUtc) =>
        new() { Status = BootstrapStatus.WindowOpen, WindowExpiresUtc = expiresUtc };

    public static BootstrapState CompletedState { get; } = new() { Status = BootstrapStatus.Completed };
}
