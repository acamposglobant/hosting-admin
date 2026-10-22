namespace HostingAdmin.Infrastructure.Linux;

/// <summary>Operating system identity parsed from /etc/os-release.</summary>
public sealed record OsReleaseInfo(string Name, string? Version);

/// <summary>
/// Defensive parser for /etc/os-release (plan §3.6): a fixed, known-safe file of
/// KEY=value pairs (values may be single- or double-quoted). Malformed lines are
/// skipped. The display name prefers PRETTY_NAME and falls back to NAME.
/// </summary>
public static class OsReleaseParser
{
    public static OsReleaseInfo? Parse(string content)
    {
        string? name = null;
        string? prettyName = null;
        string? versionId = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = Unquote(trimmed[(separator + 1)..].Trim());

            if (key.Equals("NAME", StringComparison.Ordinal))
            {
                name = value;
            }
            else if (key.Equals("PRETTY_NAME", StringComparison.Ordinal))
            {
                prettyName = value;
            }
            else if (key.Equals("VERSION_ID", StringComparison.Ordinal))
            {
                versionId = value;
            }
        }

        var displayName = prettyName ?? name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        return new OsReleaseInfo(displayName, string.IsNullOrWhiteSpace(versionId) ? null : versionId);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
