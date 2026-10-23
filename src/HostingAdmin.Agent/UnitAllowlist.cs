namespace HostingAdmin.Agent;

/// <summary>
/// Agent-side unit allowlist (D18): the agent independently re-validates every
/// request against its own allowlist copy and never trusts API-side validation
/// (defense in depth across the trust boundary).
///
/// Rules:
/// - Exact, ordinal, case-sensitive string match — no wildcards, no normalization.
/// - Strict charset <c>[A-Za-z0-9_.@-]+</c>; no path separators, no whitespace.
/// - No leading dot or dash (a leading dash could be parsed as an option by the
///   child process even in fixed-argv position; belt and braces).
/// </summary>
public sealed class UnitAllowlist
{
    public const string ValidCharset = "A-Za-z0-9_.@-";

    private readonly HashSet<string> _units;

    public UnitAllowlist(IEnumerable<string> unitNames)
    {
        _units = new HashSet<string>(unitNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when the unit is an exact allowlist entry AND passes the strict
    /// charset rule. The allowlist match alone is sufficient (entries are
    /// validated at startup), but both checks run on every request.
    /// </summary>
    public bool Contains(string unit)
    {
        if (string.IsNullOrEmpty(unit) || unit.Length > 256)
        {
            return false;
        }
        return _units.Contains(unit) && PassesCharset(unit);
    }

    /// <summary>
    /// Strict charset check, independent of the allowlist contents. Applied to
    /// every request so a poisoned allowlist entry cannot silently widen what
    /// reaches the command builder.
    /// </summary>
    public static bool PassesCharset(string unit)
    {
        if (unit.Length == 0 || unit.Length > 256)
        {
            return false;
        }
        if (unit[0] is '.' or '-')
        {
            return false;
        }
        foreach (var c in unit)
        {
            var ok = c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '@' or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Startup validation of the configured allowlist (fail-fast, D22):
    /// returns configuration errors; the agent refuses to start when non-empty.
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<string>? unitNames)
    {
        var errors = new List<string>();
        if (unitNames is null || !unitNames.Any())
        {
            errors.Add("Agent:UnitNames must list at least one systemd unit — " +
                       "the agent exists only to act on allowlisted units.");
            return errors;
        }

        foreach (var unit in unitNames)
        {
            if (string.IsNullOrWhiteSpace(unit))
            {
                errors.Add("Agent:UnitNames contains an empty entry.");
            }
            else if (!PassesCharset(unit))
            {
                errors.Add(
                    $"Agent:UnitNames entry '{unit}' violates the strict charset rule " +
                    $"([{ValidCharset}]+, no leading dot or dash).");
            }
        }

        return errors;
    }
}
