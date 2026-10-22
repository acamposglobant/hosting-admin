using System.Security.Cryptography;

namespace HostingAdmin.Core.Auth;

/// <summary>
/// Password hashing with PBKDF2-HMAC-SHA256 (plan §3.3: Argon2id/PBKDF2).
/// Uses the in-box <see cref="Rfc2898DeriveBytes"/> — no external dependency.
/// Storage format: "pbkdf2-sha256$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;hash base64&gt;".
/// Verification uses a fixed-time comparison to avoid timing oracles.
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP-recommended iteration count for PBKDF2-HMAC-SHA256 (600,000).</summary>
    public const int Iterations = 600_000;

    private const int SaltByteCount = 16;
    private const int HashByteCount = 32;
    private const string FormatPrefix = "pbkdf2-sha256";

    /// <summary>Hashes a password for storage. Never log the input or output (D7).</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltByteCount);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, HashByteCount);

        return $"{FormatPrefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a password against a stored hash. Returns false for malformed
    /// or unknown-format stored values (never throws on bad input).
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != FormatPrefix)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
