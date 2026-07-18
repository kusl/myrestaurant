namespace MyRestaurant.Domain.Security;

/// <summary>
/// Encoding, parsing, and the rehash decision for the Argon2id PHC string stored in
/// <c>person.password_hash</c> (TECHNICAL_SPECIFICATION §3.2):
/// <code>$argon2id$v=19$m=65536,t=3,p=1$&lt;base64-no-pad(salt)&gt;$&lt;base64-no-pad(tag)&gt;</code>
/// This type is intentionally pure string/Base64 work — it does NOT compute Argon2. The
/// custom <c>IPasswordHasher</c> (M2, in the web layer over Konscious.Security.Cryptography)
/// computes the raw tag, encodes it here, and on verify parses the STORED parameters,
/// recomputes, compares with <c>CryptographicOperations.FixedTimeEquals</c>, and rehashes
/// when <see cref="NeedsRehash"/> is true. Argon2 encoded hashes use the standard Base64
/// alphabet (not URL-safe), without padding.
/// </summary>
public static class Argon2PhcString
{
    public const int Version = 19; // Argon2 version 1.3 (0x13)
    private const string AlgorithmLabel = "argon2id";

    public static string Encode(Argon2Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return $"${AlgorithmLabel}$v={Version}$m={parameters.MemoryKibibytes},t={parameters.Iterations},p={parameters.Parallelism}$" +
               $"{StandardBase64NoPadding(parameters.Salt)}${StandardBase64NoPadding(parameters.Tag)}";
    }

    public static Argon2Parameters Parse(string phcString)
    {
        if (!TryParse(phcString, out Argon2Parameters? parameters))
        {
            throw new FormatException("The value is not a well-formed argon2id PHC string.");
        }

        return parameters;
    }

    public static bool TryParse(string phcString, out Argon2Parameters? parameters)
    {
        parameters = null;
        if (string.IsNullOrEmpty(phcString))
        {
            return false;
        }

        // Leading '$' yields an empty first segment: ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, tag]
        string[] segments = phcString.Split('$');
        if (segments.Length != 6 || segments[0].Length != 0)
        {
            return false;
        }

        if (!string.Equals(segments[1], AlgorithmLabel, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseTaggedInteger(segments[2], "v", out int version) || version != Version)
        {
            return false;
        }

        string[] costs = segments[3].Split(',');
        if (costs.Length != 3
            || !TryParseTaggedInteger(costs[0], "m", out int memoryKibibytes)
            || !TryParseTaggedInteger(costs[1], "t", out int iterations)
            || !TryParseTaggedInteger(costs[2], "p", out int parallelism))
        {
            return false;
        }

        if (!TryDecodeStandardBase64NoPadding(segments[4], out byte[] salt)
            || !TryDecodeStandardBase64NoPadding(segments[5], out byte[] tag))
        {
            return false;
        }

        parameters = new Argon2Parameters(memoryKibibytes, iterations, parallelism, salt, tag);
        return true;
    }

    /// <summary>
    /// True when the stored cost parameters differ from the currently configured ones, so
    /// the verifier should transparently rehash at sign-in (Identity's SuccessRehashNeeded).
    /// The salt and tag never enter this decision.
    /// </summary>
    public static bool NeedsRehash(Argon2Parameters stored, int configuredMemoryKibibytes, int configuredIterations, int configuredParallelism)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return stored.MemoryKibibytes != configuredMemoryKibibytes
            || stored.Iterations != configuredIterations
            || stored.Parallelism != configuredParallelism;
    }

    private static bool TryParseTaggedInteger(string segment, string expectedTag, out int value)
    {
        value = 0;
        int equals = segment.IndexOf('=');
        if (equals != expectedTag.Length
            || !segment.AsSpan(0, equals).SequenceEqual(expectedTag))
        {
            return false;
        }

        return int.TryParse(segment.AsSpan(equals + 1), out value);
    }

    private static string StandardBase64NoPadding(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecodeStandardBase64NoPadding(string text, out byte[] bytes)
    {
        bytes = [];
        string padded = (text.Length % 4) switch
        {
            2 => text + "==",
            3 => text + "=",
            1 => text, // invalid; Convert will reject
            _ => text,
        };

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The parsed contents of an Argon2id PHC string.</summary>
public sealed record Argon2Parameters(int MemoryKibibytes, int Iterations, int Parallelism, byte[] Salt, byte[] Tag);
