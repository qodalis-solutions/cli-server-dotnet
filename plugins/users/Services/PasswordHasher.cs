using System.Security.Cryptography;
using System.Text;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (100,000 iterations, 16-byte salt, 32-byte key),
/// encoded as <c>pbkdf2$100000$&lt;saltB64&gt;$&lt;hashB64&gt;</c>. Verification is constant-time.
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 100_000;
    public const int SaltSize = 16;
    public const int KeySize = 32;
    private const string Prefix = "pbkdf2";

    /// <summary>Hashes <paramref name="password"/> with a new random salt.</summary>
    public static (string Encoded, string SaltB64) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        var saltB64 = Convert.ToBase64String(salt);
        return ($"{Prefix}${Iterations}${saltB64}${Convert.ToBase64String(key)}", saltB64);
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against an encoded hash. Also accepts the legacy
    /// format (hex SHA-256 of <c>salt + password</c> with a separate hex salt).
    /// </summary>
    public static bool Verify(string password, string encoded, string? legacySalt = null)
    {
        if (string.IsNullOrEmpty(encoded)) return false;

        var parts = encoded.Split('$');
        if (parts.Length == 4 && parts[0] == Prefix)
        {
            if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;
            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        if (legacySalt == null) return false;
        var legacy = SHA256.HashData(Encoding.UTF8.GetBytes(legacySalt + password));
        var legacyExpected = Encoding.ASCII.GetBytes(encoded.ToLowerInvariant());
        var legacyActual = Encoding.ASCII.GetBytes(Convert.ToHexString(legacy).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(legacyActual, legacyExpected);
    }

    /// <summary>True when the hash is not in the current PBKDF2 format and should be re-hashed.</summary>
    public static bool NeedsRehash(string encoded) =>
        !encoded.StartsWith(Prefix + "$" + Iterations + "$", StringComparison.Ordinal);
}
