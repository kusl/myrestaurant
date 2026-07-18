using System.Security.Cryptography;
using System.Text;

namespace MyRestaurant.Domain.Security;

/// <summary>
/// SHA-256 for the non-password secrets that are stored hashed: TOTP recovery codes,
/// display pairing codes, and display-device secrets (TECHNICAL_SPECIFICATION §3.4, §4.2).
/// Passwords use Argon2id instead (§3.2) — never route a password through here.
/// Comparisons against stored hashes MUST use <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
public static class Sha256Hashing
{
    public const int HashByteCount = 32;

    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>Constant-time comparison of a candidate against a stored hash.</summary>
    public static bool MatchesStoredHash(string candidate, ReadOnlySpan<byte> storedHash)
        => CryptographicOperations.FixedTimeEquals(Hash(candidate), storedHash);
}
