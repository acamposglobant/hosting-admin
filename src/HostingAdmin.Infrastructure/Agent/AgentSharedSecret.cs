using System.Text;

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

        // Strip all leading/trailing whitespace (including \r\n from Windows
        // line endings) so the bytes match the agent-side loader, which trims
        // the same way (review F4). A mismatch here would make the agent
        // reject every request with a non-obvious auth failure.
        var trimmed = Encoding.UTF8.GetString(secret).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                $"Agent shared secret file contains only whitespace: {secretFile}");
        }

        // Minimum length (review F6): matches the agent-side check so a weak
        // secret fails fast at startup on either side instead of passing a
        // brute-forceable key into production.
        if (trimmed.Length < 32)
        {
            throw new InvalidOperationException(
                $"Agent shared secret must be at least 32 characters: {secretFile}");
        }

        return Encoding.UTF8.GetBytes(trimmed);
    }
}
