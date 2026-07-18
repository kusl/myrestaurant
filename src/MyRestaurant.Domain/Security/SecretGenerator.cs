using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

/// <summary>
/// Cryptographically secure random material: table join secrets (32 bytes, §4.1),
/// display-device secrets (§4.2), TOTP secrets (20 bytes, §3.4), and anything else that
/// must be unguessable. Never uses <see cref="System.Random"/>.
/// </summary>
public static class SecretGenerator
{
    /// <summary>The 32-byte per-table join secret (TECHNICAL_SPECIFICATION §4.1).</summary>
    public const int JoinSecretByteCount = 32;

    /// <summary>The 20-byte TOTP shared secret (RFC 6238; TECHNICAL_SPECIFICATION §3.4).</summary>
    public const int TotpSecretByteCount = 20;

    /// <summary>The 32-byte display-device secret (TECHNICAL_SPECIFICATION §4.2).</summary>
    public const int DeviceSecretByteCount = 32;

    public static byte[] GenerateBytes(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 1);
        return RandomNumberGenerator.GetBytes(byteCount);
    }

    public static byte[] GenerateJoinSecret() => GenerateBytes(JoinSecretByteCount);

    public static byte[] GenerateTotpSecret() => GenerateBytes(TotpSecretByteCount);

    /// <summary>Random bytes rendered as Base64Url text (e.g. the device cookie secret).</summary>
    public static string GenerateBase64UrlSecret(int byteCount) => Base64UrlText.Encode(GenerateBytes(byteCount));
}
