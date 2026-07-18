using System.Security.Cryptography;

namespace MyRestaurant.Domain.Security;

/// <summary>
/// TOTP recovery codes (TECHNICAL_SPECIFICATION §3.4): ten single-use codes generated at
/// enrollment and on regeneration, stored hashed (SHA-256), usable only on the password
/// sign-in path in place of a TOTP code. Rendered as two dash-separated groups from the
/// same unambiguous alphabet as pairing codes.
/// </summary>
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

    /// <summary>A fresh, de-duplicated set of <see cref="CodesPerSet"/> recovery codes.</summary>
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
