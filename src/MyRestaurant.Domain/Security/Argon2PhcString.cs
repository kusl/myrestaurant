using System.Diagnostics.CodeAnalysis;

namespace MyRestaurant.Domain.Security;

public static class Argon2PhcString
{
    public const int Version = 19;
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

    public static bool TryParse(string phcString, [NotNullWhen(true)] out Argon2Parameters? parameters)
    {
        parameters = null;
        if (string.IsNullOrEmpty(phcString))
        {
            return false;
        }

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
            1 => text,
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

public sealed record Argon2Parameters(int MemoryKibibytes, int Iterations, int Parallelism, byte[] Salt, byte[] Tag);
