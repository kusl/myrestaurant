using System.Security.Cryptography;
using System.Text;

namespace MyRestaurant.Domain.Security;

public static class Sha256Hashing
{
    public const int HashByteCount = 32;

    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public static bool MatchesStoredHash(string candidate, ReadOnlySpan<byte> storedHash)
        => CryptographicOperations.FixedTimeEquals(Hash(candidate), storedHash);
}
