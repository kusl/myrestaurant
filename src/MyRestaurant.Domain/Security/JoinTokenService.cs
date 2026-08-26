using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyRestaurant.Domain.Security;

public enum JoinTokenValidationResult
{
    Valid,
    Expired,
    Invalid,
}

public static class JoinTokenService
{
    public const int DefaultExpiredLookbackWindows = 10;

    public static long CurrentWindowIndex(DateTimeOffset instant, int rotationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rotationSeconds, 1);
        return instant.ToUnixTimeSeconds() / rotationSeconds;
    }

    public static string ComputeToken(ReadOnlySpan<byte> joinSecret, Guid tableIdentifier, long windowIndex)
    {
        string message = string.Concat(
            tableIdentifier.ToString("D").ToLowerInvariant(),
            ":",
            windowIndex.ToString(CultureInfo.InvariantCulture));

        byte[] mac = HMACSHA256.HashData(joinSecret, Encoding.UTF8.GetBytes(message));
        return Base64UrlText.Encode(mac);
    }

    public static string ComputeCurrentToken(ReadOnlySpan<byte> joinSecret, Guid tableIdentifier, DateTimeOffset instant, int rotationSeconds)
        => ComputeToken(joinSecret, tableIdentifier, CurrentWindowIndex(instant, rotationSeconds));

    public static string BuildJoinUrl(string publicOrigin, Guid tableIdentifier, string token)
        => $"{publicOrigin.TrimEnd('/')}/table/{tableIdentifier:D}?token={token}";

    public static DateTimeOffset NextRotationInstant(DateTimeOffset instant, int rotationSeconds)
    {
        long nextWindow = CurrentWindowIndex(instant, rotationSeconds) + 1;
        return DateTimeOffset.FromUnixTimeSeconds(nextWindow * rotationSeconds);
    }

    public static JoinTokenValidationResult Validate(
        ReadOnlySpan<byte> joinSecret,
        Guid tableIdentifier,
        string presentedToken,
        DateTimeOffset instant,
        int rotationSeconds,
        int expiredLookbackWindows = DefaultExpiredLookbackWindows)
    {
        if (!Base64UrlText.TryDecode(presentedToken, out byte[] presented) || presented.Length != Sha256Hashing.HashByteCount)
        {
            return JoinTokenValidationResult.Invalid;
        }

        long currentWindow = CurrentWindowIndex(instant, rotationSeconds);

        if (MatchesWindow(joinSecret, tableIdentifier, currentWindow, presented)
            || MatchesWindow(joinSecret, tableIdentifier, currentWindow - 1, presented))
        {
            return JoinTokenValidationResult.Valid;
        }

        for (long window = currentWindow - 2; window >= currentWindow - 1 - expiredLookbackWindows; window--)
        {
            if (MatchesWindow(joinSecret, tableIdentifier, window, presented))
            {
                return JoinTokenValidationResult.Expired;
            }
        }

        return JoinTokenValidationResult.Invalid;
    }

    private static bool MatchesWindow(ReadOnlySpan<byte> joinSecret, Guid tableIdentifier, long windowIndex, ReadOnlySpan<byte> presented)
    {
        string message = string.Concat(
            tableIdentifier.ToString("D").ToLowerInvariant(),
            ":",
            windowIndex.ToString(CultureInfo.InvariantCulture));

        byte[] computed = HMACSHA256.HashData(joinSecret, Encoding.UTF8.GetBytes(message));
        return CryptographicOperations.FixedTimeEquals(computed, presented);
    }
}
