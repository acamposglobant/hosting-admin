namespace HostingAdmin.Core.Bootstrap;

/// <summary>
/// First-admin bootstrap configuration (plan D6): the initialization window is
/// short and operator-configurable; the password policy is enforced at creation.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>
    /// How long the initialization window stays open after local activation.
    /// Default 15 minutes; startup validation caps it at one hour so a
    /// misconfiguration cannot leave bootstrap open indefinitely.
    /// </summary>
    public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum accepted admin password length. Default 12.</summary>
    public int MinPasswordLength { get; set; } = 12;

    /// <summary>Maximum accepted admin password length (bounds hashing work). Default 128.</summary>
    public int MaxPasswordLength { get; set; } = 128;
}
