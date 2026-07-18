using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyRestaurant.Domain.Security;

/// <summary>The outcome of validating a presented join token (TECHNICAL_SPECIFICATION §4.3).</summary>
public enum JoinTokenValidationResult
{
    /// <summary>Matches the current or immediately previous window; the join may proceed.</summary>
    Valid,

    /// <summary>Matches a recent-but-older window (bounded lookback); for metric labelling only.</summary>
    Expired,

    /// <summary>No match, malformed, or older than the lookback.</summary>
    Invalid,
}

/// <summary>
/// The rotating per-table join token (ADR-0009, TECHNICAL_SPECIFICATION §4.3), normatively:
/// <code>
/// window_index = floor(unix_time_seconds / rotation)
/// message      = UTF8( lowercase-hyphenated-table-uuid + ":" + decimal(window_index) )
/// token        = Base64Url( HMAC_SHA256(join_secret, message) )   // 32 bytes, no padding
/// </code>
/// The join secret never leaves the server; displays and the counter fallback receive only a
/// rendered QR. Validation accepts the current and previous window (worst-case life 2×rotation).
/// </summary>
public static class JoinTokenService
{
    /// <summary>Windows older than the previous, up to this many, are classified Expired (metrics only).</summary>
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

    /// <summary>The scan URL embedded in the QR: <c>{origin}/table/{table}?token={token}</c>.</summary>
    public static string BuildJoinUrl(string publicOrigin, Guid tableIdentifier, string token)
        => $"{publicOrigin.TrimEnd('/')}/table/{tableIdentifier:D}?token={token}";

    /// <summary>
    /// The next UTC instant at which the token rotates — the display re-renders on a timer
    /// aligned to <c>(window_index + 1) × rotation</c> (TECHNICAL_SPECIFICATION §4.3).
    /// </summary>
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

        // Accept the current and immediately previous window.
        if (MatchesWindow(joinSecret, tableIdentifier, currentWindow, presented)
            || MatchesWindow(joinSecret, tableIdentifier, currentWindow - 1, presented))
        {
            return JoinTokenValidationResult.Valid;
        }

        // Older-but-recent windows are Expired (label only); anything else is Invalid.
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
