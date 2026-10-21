using System.Text.RegularExpressions;

namespace HostingAdmin.Core.Logging;

/// <summary>
/// Denylist-based redaction of secret material (plan D7).
/// Passwords, session identifiers, session secrets, proxy secrets, tokens, and
/// credential material must never appear in logs, audit records, or responses.
/// </summary>
public static partial class SecretRedactor
{
    public const string RedactedPlaceholder = "***REDACTED***";

    /// <summary>
    /// Key fragments that trigger redaction. Matching is case-insensitive and
    /// substring-based so variants like "AdminPassword", "proxy_secret" or
    /// "SessionId" are all caught. Biased toward false positives (e.g. "monkey"
    /// matches "key") — for a denylist that is the correct failure mode.
    /// </summary>
    private static readonly string[] DenylistFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "credential",
        "apikey",
        "api_key",
        "privatekey",
        "private_key",
        "private",
        "authorization",
        "cookie",
        "sessionid",
        "session_id",
        "session",
        "bearer",
        "pwd",
        "key",
    ];

    [GeneratedRegex(
        """("(?:[^"\\]|\\.)*")\s*:\s*("(?:[^"\\]|\\.)*")""",
        RegexOptions.NonBacktracking)]
    private static partial Regex JsonStringPairRegex();

    // key=value / key=value&... patterns (query strings, connection strings, logfmt).
    [GeneratedRegex(
        """([A-Za-z_][A-Za-z0-9_\-\.]*)=([^\s&;"]+)""",
        RegexOptions.NonBacktracking)]
    private static partial Regex KeyValueRegex();

    // Authorization: Bearer <token> / Basic <credentials>.
    [GeneratedRegex(
        """(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/=]+""",
        RegexOptions.NonBacktracking)]
    private static partial Regex AuthSchemeRegex();

    /// <summary>
    /// Redacts secret material in an arbitrary log string:
    /// JSON "key":"value" pairs, key=value pairs, and Bearer/Basic credentials.
    /// Used as a last-resort scrubber for structured log messages and state.
    /// </summary>
    public static string RedactJsonString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var jsonScrubbed = JsonStringPairRegex().Replace(
            input,
            match => IsDenylistedKey(match.Groups[1].Value)
                ? $"{match.Groups[1].Value}: \"{RedactedPlaceholder}\""
                : match.Value);

        var keyValueScrubbed = KeyValueRegex().Replace(
            jsonScrubbed,
            match => IsDenylistedKey(match.Groups[1].Value)
                ? $"{match.Groups[1].Value}={RedactedPlaceholder}"
                : match.Value);

        return AuthSchemeRegex().Replace(
            keyValueScrubbed,
            match => $"{match.Groups[1].Value} {RedactedPlaceholder}");
    }

    /// <summary>
    /// Redacts a single value when its key contains a denylist fragment.
    /// Returns the original value when the key is safe.
    /// </summary>
    public static string RedactValue(string key, string value) =>
        IsDenylistedKey(key) ? RedactedPlaceholder : value;

    /// <summary>Case-insensitive substring check against the denylist fragments.</summary>
    public static bool IsDenylistedKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var fragment in DenylistFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
