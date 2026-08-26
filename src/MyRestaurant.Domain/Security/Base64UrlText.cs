namespace MyRestaurant.Domain.Security;

public static class Base64UrlText
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        string standard = Convert.ToBase64String(bytes);
        return standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

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
            case 1: return false;
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
