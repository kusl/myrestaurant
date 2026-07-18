namespace MyRestaurant.Domain.Security;

/// <summary>
/// RFC 4648 §5 Base64Url with padding stripped — the exact encoding used for join tokens
/// (TECHNICAL_SPECIFICATION §4.3), device/secret material, and the salt/tag fields of the
/// Argon2 PHC string (§3.2). Implemented explicitly rather than via a framework helper so
/// the byte-for-byte output is fixed and testable against precomputed vectors.
/// </summary>
public static class Base64UrlText
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        string standard = Convert.ToBase64String(bytes);
        return standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes Base64Url text. Returns <c>false</c> (never throws) on any malformed input,
    /// so validators can treat a bad token as simply "not a match".
    /// </summary>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text is null)
        {
            return false;
        }

        string standard = text.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2: standard += "=="; break;
            case 3: standard += "="; break;
            case 1: return false; // never a valid length
        }

        try
        {
            bytes = Convert.FromBase64String(standard);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
