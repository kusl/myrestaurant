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

    /// <summary>
    /// Canonicalizes what a human typed into what was generated: upper case, with the separators people
    /// add unbidden — spaces, hyphens, and the en/em dashes some keyboards autocorrect them into —
    /// removed. The code is read off one screen and typed into another by a person standing in a
    /// restaurant, so "abcd-efgh" must be the same code as "ABCDEFGH"; nothing about the alphabet
    /// (§4.2, unambiguous, upper case, no punctuation) makes that ambiguous.
    ///
    /// <para>Normalization only reshapes: it never accepts. The result still has to pass
    /// <see cref="IsWellFormed"/>, so a code padded with stray letters is rejected exactly as before.
    /// A <c>null</c> input normalizes to the empty string rather than throwing, because the caller is a
    /// public, anonymous form post.</para>
    /// </summary>
    public static string Normalize(string? presentedCode)
    {
        // The upper bound keeps the stack allocation below fixed and small: nothing longer than this
        // could normalize to eight characters anyway, so a pasted essay is rejected before it is copied.
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
