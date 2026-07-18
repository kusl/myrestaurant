using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

/// <summary>
/// The one-time display-device pairing code (TECHNICAL_SPECIFICATION §4.2): 8 characters
/// from an unambiguous alphabet (no I/L/O/0/1). Generated with a CSPRNG and unbiased
/// selection; stored only as its SHA-256 hash. The plaintext is shown once, to a human.
/// </summary>
public static class PairingCode
{
    /// <summary>23 letters + 8 digits = 31 symbols; excludes I, L, O, and 0, 1.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    public static string Generate()
    {
        char[] buffer = new char[Length];
        for (int index = 0; index < Length; index++)
        {
            // RandomNumberGenerator.GetInt32 is unbiased over [0, Alphabet.Length).
            buffer[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }

    /// <summary>True when every character is drawn from <see cref="Alphabet"/> and the length matches.</summary>
    public static bool IsWellFormed(string code)
        => code is { Length: Length } && code.All(character => Alphabet.Contains(character, StringComparison.Ordinal));
}
