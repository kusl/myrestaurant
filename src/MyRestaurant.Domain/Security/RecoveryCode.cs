using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

public static class RecoveryCode
{
    public const int CodesPerSet = 10;
    public const int GroupLength = 5;

    public static string GenerateOne()
    {
        char[] buffer = new char[(GroupLength * 2) + 1];
        int cursor = 0;
        for (int group = 0; group < 2; group++)
        {
            if (group == 1)
            {
                buffer[cursor++] = '-';
            }

            for (int index = 0; index < GroupLength; index++)
            {
                buffer[cursor++] = PairingCode.Alphabet[RandomNumberGenerator.GetInt32(PairingCode.Alphabet.Length)];
            }
        }

        return new string(buffer);
    }

    public static IReadOnlyList<string> GenerateSet()
    {
        HashSet<string> codes = new(StringComparer.Ordinal);
        while (codes.Count < CodesPerSet)
        {
            codes.Add(GenerateOne());
        }

        return [.. codes];
    }
}
