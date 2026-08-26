using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

public static class Rfc6238Totp
{
    public const int SecretSizeInBytes = 20;

    public const int CodeLength = 6;

    public const int TimeStepSeconds = 30;

    public const int AllowedStepSkew = 1;

    private const int Modulo = 1_000_000;

    public static byte[] GenerateSecret() => SecretGenerator.GenerateTotpSecret();

    public static string ComputeCode(ReadOnlySpan<byte> secret, DateTimeOffset timestamp)
        => ComputeCodeForStep(secret, StepNumber(timestamp));

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

        int offset = mac[^1] & 0x0F;
        int binary =
            ((mac[offset] & 0x7F) << 24)
            | (mac[offset + 1] << 16)
            | (mac[offset + 2] << 8)
            | mac[offset + 3];

        return (binary % Modulo).ToString().PadLeft(CodeLength, '0');
    }
}
