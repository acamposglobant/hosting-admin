namespace HostingAdmin.Infrastructure.Agent;

/// <summary>
/// Loads the API↔agent shared secret from its mode-600 file (D17/D22). The
/// secret is held in memory only: never logged, never returned in API
/// responses (D25).
/// </summary>
public static class AgentSharedSecret
{
    /// <summary>
    /// Reads the secret file. Throws at startup if missing or empty — the API
    /// fails fast on invalid configuration (D22) rather than reporting the
    /// mutation capability as unavailable with a silent misconfiguration.
    /// </summary>
    public static byte[] Load(string secretFile)
    {
        var secret = File.ReadAllBytes(secretFile);
        if (secret.Length == 0)
        {
            throw new InvalidOperationException(
                $"Agent shared secret file is empty: {secretFile}");
        }

        // Strip a single trailing newline so editors/deployment scripts that
        // append one do not break the comparison with the agent's copy.
        if (secret[^1] == (byte)'\n')
        {
            secret = secret[..^1];
        }

        if (secret.Length == 0)
        {
            throw new InvalidOperationException(
                $"Agent shared secret file contains only whitespace: {secretFile}");
        }

        return secret;
    }
}
