namespace MyRestaurant.Domain.Security;

/// <summary>
/// RFC 4648 §6 Base32 (uppercase, padding stripped) — the encoding of the TOTP shared secret in the
/// provisioning URI and in the authenticator-key string ASP.NET Core Identity round-trips through
/// the user store (TECHNICAL_SPECIFICATION §3.4). A 20-byte secret is exactly 32 characters, so
/// padding never appears in practice. Implemented explicitly, like <see cref="Base64UrlText"/>, so
/// the byte-for-byte output is fixed and testable against the RFC vectors.
/// </summary>
public static class Base32Text
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        char[] buffer = new char[((bytes.Length * 8) + 4) / 5];
        int cursor = 0;
        int bitBuffer = 0;
        int bitCount = 0;

        foreach (byte value in bytes)
        {
            bitBuffer = (bitBuffer << 8) | value;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                buffer[cursor++] = Alphabet[(bitBuffer >> bitCount) & 0x1F];
            }
        }

        if (bitCount > 0)
        {
            buffer[cursor++] = Alphabet[(bitBuffer << (5 - bitCount)) & 0x1F];
        }

        return new string(buffer, 0, cursor);
    }

    /// <summary>
    /// Decodes Base32 text. Returns <c>false</c> (never throws) on any malformed input. Forgiving of
    /// lowercase, of the spaces/dashes secrets are displayed with, and of trailing <c>'='</c>
    /// padding — but impossible lengths and nonzero leftover bits are rejected rather than silently
    /// dropped, so a truncated or corrupted secret never half-decodes.
    /// </summary>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text is null)
        {
            return false;
        }

        // Trailing '=' padding is legal; '=' anywhere else is not (it falls through to the
        // alphabet check below and is rejected there).
        int end = text.Length;
        while (end > 0 && text[end - 1] == '=')
        {
            end--;
        }

        List<byte> output = new(end * 5 / 8);
        int bitBuffer = 0;
        int bitCount = 0;

        for (int index = 0; index < end; index++)
        {
            char character = char.ToUpperInvariant(text[index]);
            if (character is ' ' or '-')
            {
                continue; // grouped-display separators
            }

            int value;
            if (character is >= 'A' and <= 'Z')
            {
                value = character - 'A';
            }
            else if (character is >= '2' and <= '7')
            {
                value = character - '2' + 26;
            }
            else
            {
                return false;
            }

            bitBuffer = (bitBuffer << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                output.Add((byte)((bitBuffer >> bitCount) & 0xFF));
            }
        }

        // Five or more leftover bits means an impossible encoded length (1, 3, or 6 characters
        // mod 8); fewer leftover bits must all be zero, or the tail was truncated or corrupted.
        if (bitCount >= 5 || (bitBuffer & ((1 << bitCount) - 1)) != 0)
        {
            return false;
        }

        bytes = [.. output];
        return true;
    }
}
