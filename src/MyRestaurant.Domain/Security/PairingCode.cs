using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

public static class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    public static string Generate()
    {
        char[] buffer = new char[Length];
        for (int index = 0; index < Length; index++)
        {
            buffer[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }

    public static bool IsWellFormed(string code)
        => code is { Length: Length } && code.All(character => Alphabet.Contains(character, StringComparison.Ordinal));

    public static string Normalize(string? presentedCode)
    {
        const int maximumPresentedLength = 128;

        if (string.IsNullOrWhiteSpace(presentedCode) || presentedCode.Length > maximumPresentedLength)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[maximumPresentedLength];
        int written = 0;

        foreach (char character in presentedCode)
        {
            if (char.IsWhiteSpace(character) || IsSeparator(character))
            {
                continue;
            }

            buffer[written++] = char.ToUpperInvariant(character);
        }

        return new string(buffer[..written]);
    }

    private static bool IsSeparator(char character)
        => character is '-' or '_' or '.' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014';
}
