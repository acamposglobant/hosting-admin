using System.Text.RegularExpressions;

namespace HostingAdmin.Core.Services;

/// <summary>
/// Service status options (plan task 12, D12): the unit allowlist is the only
/// source of unit names, validated at startup. Invalid configuration must fail
/// startup rather than degrade silently.
/// </summary>
public sealed class ServiceStatusOptions
{
    public const string SectionName = "Services";

    /// <summary>
    /// The systemd units to report. The client never supplies unit names (D12).
    /// </summary>
    public IReadOnlyList<string> UnitNames { get; set; } = [];

    /// <summary>Absolute path to the systemctl binary.</summary>
    public string SystemctlPath { get; set; } = "/usr/bin/systemctl";

    /// <summary>Hard upper bound for one systemctl invocation.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// systemd unit name grammar (daemons(7), systemd.unit(5)): the primary
    /// portion allows ASCII letters, digits and : _ . \ - @ (the latter for
    /// template units) and must contain at least one '.' separating name and
    /// type suffix; the suffix is letters only. Anything else is rejected
    /// before any process is started.
    /// </summary>
    private static readonly Regex UnitNamePattern =
        new(@"^[A-Za-z0-9:_.@\\-]+\.[A-Za-z]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns true when <paramref name="unitName"/> is a syntactically valid
    /// systemd unit name. This is a defense-in-depth check on top of the
    /// allowlist: only names from the validated allowlist are ever used.
    /// </summary>
    public static bool IsValidUnitName(string unitName)
    {
        if (string.IsNullOrEmpty(unitName) || unitName.Length > 255)
        {
            return false;
        }

        return UnitNamePattern.IsMatch(unitName);
    }
}
