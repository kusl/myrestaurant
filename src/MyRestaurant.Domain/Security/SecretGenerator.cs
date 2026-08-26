using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

public static class SecretGenerator
{
    public const int JoinSecretByteCount = 32;

    public const int TotpSecretByteCount = 20;

    public const int DeviceSecretByteCount = 32;

    public static byte[] GenerateBytes(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 1);
        return RandomNumberGenerator.GetBytes(byteCount);
    }

    public static byte[] GenerateJoinSecret() => GenerateBytes(JoinSecretByteCount);

    public static byte[] GenerateTotpSecret() => GenerateBytes(TotpSecretByteCount);

    public static string GenerateBase64UrlSecret(int byteCount) => Base64UrlText.Encode(GenerateBytes(byteCount));
}
