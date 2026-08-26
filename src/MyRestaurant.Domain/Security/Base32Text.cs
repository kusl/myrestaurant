namespace MyRestaurant.Domain.Security;

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

    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text is null)
        {
            return false;
        }

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
                continue;
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

        if (bitCount >= 5 || (bitBuffer & ((1 << bitCount) - 1)) != 0)
        {
            return false;
        }

        bytes = [.. output];
        return true;
    }
}
