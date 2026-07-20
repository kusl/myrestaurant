using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords (TECHNICAL_SPECIFICATION §3.4): HMAC-SHA-1, 6 digits, a
/// 30-second step, and a <b>±1 step</b> acceptance window — one step either side of the current one,
/// to tolerate clock skew and the moment of typing without opening the wider window Identity's
/// built-in provider uses. The RFC 4226 dynamic-truncation step and the big-endian counter are
/// implemented directly so the output is fixed and testable against the published Appendix B vectors;
/// the code comparison is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>).
///
/// <para>This is the pure engine. The Identity-facing wrapper that reads the key from the user store
/// and is dispatched by <c>VerifyTwoFactorTokenAsync</c> is
/// <c>RestaurantAuthenticatorTokenProvider</c> in the web layer.</para>
/// </summary>
public static class Rfc6238Totp
{
    /// <summary>The RFC 6238 shared-secret size (§3.4); mirrors <see cref="SecretGenerator.TotpSecretByteCount"/>.</summary>
    public const int SecretSizeInBytes = 20;

    /// <summary>Digits in a code (§3.4).</summary>
    public const int CodeLength = 6;

    /// <summary>The time step, in seconds (§3.4).</summary>
    public const int TimeStepSeconds = 30;

    /// <summary>Steps accepted either side of the current one (§3.4: ±1).</summary>
    public const int AllowedStepSkew = 1;

    private const int Modulo = 1_000_000; // 10^CodeLength

    /// <summary>A fresh 20-byte secret (delegates to the shared CSPRNG helper).</summary>
    public static byte[] GenerateSecret() => SecretGenerator.GenerateTotpSecret();

    /// <summary>
    /// The 6-digit code for <paramref name="secret"/> at <paramref name="timestamp"/>, zero-padded.
    /// </summary>
    public static string ComputeCode(ReadOnlySpan<byte> secret, DateTimeOffset timestamp)
        => ComputeCodeForStep(secret, StepNumber(timestamp));

    /// <summary>
    /// True when <paramref name="code"/> matches <paramref name="secret"/> within <paramref name="allowedStepSkew"/>
    /// steps of <paramref name="timestamp"/>. Non-numeric, wrong-length, and empty input all return
    /// <c>false</c> rather than throwing, so callers can treat a bad code as simply "no match".
    /// Negative absolute step numbers (timestamps before the Unix epoch) are skipped.
    /// </summary>
    public static bool ValidateCode(
        ReadOnlySpan<byte> secret,
        string code,
        DateTimeOffset timestamp,
        int allowedStepSkew = AllowedStepSkew)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowedStepSkew);

        if (code is null || code.Length != CodeLength)
        {
            return false;
        }

        foreach (char character in code)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        long currentStep = StepNumber(timestamp);
        ReadOnlySpan<byte> candidate = System.Text.Encoding.ASCII.GetBytes(code);

        bool matched = false;
        for (int offset = -allowedStepSkew; offset <= allowedStepSkew; offset++)
        {
            long step = currentStep + offset;
            if (step < 0)
            {
                continue;
            }

            byte[] expected = System.Text.Encoding.ASCII.GetBytes(ComputeCodeForStep(secret, step));

            // Compare every step (no early break) so acceptance timing does not reveal which step matched.
            matched |= CryptographicOperations.FixedTimeEquals(candidate, expected);
        }

        return matched;
    }

    private static long StepNumber(DateTimeOffset timestamp)
        => timestamp.ToUnixTimeSeconds() / TimeStepSeconds;

    private static string ComputeCodeForStep(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, mac);

        // RFC 4226 §5.3 dynamic truncation.
        int offset = mac[^1] & 0x0F;
        int binary =
            ((mac[offset] & 0x7F) << 24)
            | (mac[offset + 1] << 16)
            | (mac[offset + 2] << 8)
            | mac[offset + 3];

        return (binary % Modulo).ToString().PadLeft(CodeLength, '0');
    }
}
