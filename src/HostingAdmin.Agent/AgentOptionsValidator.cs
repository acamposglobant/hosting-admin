using System.Text;

namespace HostingAdmin.Agent;

/// <summary>
/// Startup validation for <see cref="AgentOptions"/> (D22): the agent refuses
/// to start on invalid configuration rather than degrading at request time.
/// </summary>
public static class AgentOptionsValidator
{
    /// <summary>
    /// Validates the options and returns the list of configuration errors
    /// (empty when valid). Pure function so it is directly unit-testable.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        // Socket path: absolute, no traversal, no NUL. The agent must never
        // bind anything but a unix socket at a fixed, operator-chosen path.
        if (string.IsNullOrWhiteSpace(options.SocketPath))
        {
            errors.Add("Agent:SocketPath must be configured.");
        }
        else if (!Path.IsPathRooted(options.SocketPath))
        {
            errors.Add("Agent:SocketPath must be an absolute path.");
        }
        else if (options.SocketPath.Contains('\0'))
        {
            errors.Add("Agent:SocketPath must not contain NUL characters.");
        }

        // Secret file: must be configured. Existence is checked at startup on
        // the host (ValidateOnHost below); a missing file is a hard error there.
        if (string.IsNullOrWhiteSpace(options.SecretFile))
        {
            errors.Add("Agent:SecretFile must be configured.");
        }
        else if (!Path.IsPathRooted(options.SecretFile))
        {
            errors.Add("Agent:SecretFile must be an absolute path.");
        }

        if (options.MaxConcurrentConnections < 1)
        {
            errors.Add("Agent:MaxConcurrentConnections must be at least 1.");
        }

        if (options.RequestTimeoutSeconds < 1)
        {
            errors.Add("Agent:RequestTimeoutSeconds must be at least 1.");
        }

        if (options.MaxRequestBytes < 1)
        {
            errors.Add("Agent:MaxRequestBytes must be at least 1.");
        }

        return errors;
    }

    /// <summary>
    /// Host-only checks that touch the filesystem: the secret file must exist
    /// and must not be world-readable (defense in depth around D17/D25 — the
    /// secret never leaves the host and never enters logs).
    /// </summary>
    public static IReadOnlyList<string> ValidateOnHost(AgentOptions options)
    {
        var errors = new List<string>(Validate(options));

        if (errors.Count == 0 && !File.Exists(options.SecretFile))
        {
            errors.Add($"Agent:SecretFile does not exist: '{options.SecretFile}'.");
        }

        return errors;
    }

    /// <summary>
    /// Loads the shared secret from disk. The secret is trimmed of surrounding
    /// whitespace/newline and never logged (D25).
    /// </summary>
    public static byte[] LoadSecret(string secretFile)
    {
        var text = File.ReadAllText(secretFile).Trim();
        if (text.Length < 32)
        {
            throw new InvalidOperationException(
                "The agent shared secret must be at least 32 characters.");
        }
        return Encoding.UTF8.GetBytes(text);
    }
}
