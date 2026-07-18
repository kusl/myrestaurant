using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Locks the rotating join-token algorithm (TECHNICAL_SPECIFICATION §4.3) to precomputed vectors so
/// any drift in the message construction, HMAC, or Base64Url encoding is caught. The expected tokens
/// were computed independently (Python <c>hmac</c>) and must not be regenerated from this code.
/// </summary>
public sealed class JoinTokenServiceTests
{
    // Vector 1: join_secret = bytes 0x00..0x1F, table = ...0001.
    private static readonly byte[] Vector1Secret = CreateSequentialSecret();
    private static readonly Guid Vector1Table = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Vector1Window0Token = "-XXFSZhMKikHoFW_xlwnqNqv4M46LzZhw7ISAC6QVW0";
    private const string Vector1Window1Token = "5JDkdy7zOopIM67-6k2i2eX8MgyrcTFpGAuylQ5X_jk";

    // Vector 2: join_secret = 0xAB x 32, table = deadbeef-..., window 28840320.
    private static readonly byte[] Vector2Secret = CreateRepeatedSecret(0xAB, 32);
    private static readonly Guid Vector2Table = Guid.Parse("deadbeef-0000-4000-8000-000000000000");
    private const long Vector2Window = 28_840_320L;
    private const string Vector2Token = "OkLgYBLEfDT_gTmqh4JsprGwxoatHpJ9NHeaKns8NPI";

    [Fact]
    public void ComputeToken_MatchesVector1_Window0()
        => Assert.Equal(Vector1Window0Token, JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, 0));

    [Fact]
    public void ComputeToken_MatchesVector1_Window1()
        => Assert.Equal(Vector1Window1Token, JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, 1));

    [Fact]
    public void ComputeToken_MatchesVector2()
        => Assert.Equal(Vector2Token, JoinTokenService.ComputeToken(Vector2Secret, Vector2Table, Vector2Window));

    [Fact]
    public void CurrentWindowIndex_IsUnixTimeDividedByRotation()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600); // 2026-07-17T12:00:00Z
        Assert.Equal(29_738_160L, JoinTokenService.CurrentWindowIndex(instant, 60));
    }

    [Fact]
    public void CurrentWindowIndex_IsStableWithinAWindow()
    {
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        DateTimeOffset later = start.AddSeconds(37);
        Assert.Equal(
            JoinTokenService.CurrentWindowIndex(start, 60),
            JoinTokenService.CurrentWindowIndex(later, 60));
    }

    [Fact]
    public void CurrentWindowIndex_RejectsNonPositiveRotation()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => JoinTokenService.CurrentWindowIndex(DateTimeOffset.UnixEpoch, 0));

    [Fact]
    public void ComputeCurrentToken_UsesTheCurrentWindow()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        long window = JoinTokenService.CurrentWindowIndex(instant, 60);

        Assert.Equal(
            JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, window),
            JoinTokenService.ComputeCurrentToken(Vector1Secret, Vector1Table, instant, 60));
    }

    [Fact]
    public void NextRotationInstant_IsTheStartOfTheNextWindow()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_637); // mid-window
        DateTimeOffset next = JoinTokenService.NextRotationInstant(instant, 60);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_784_289_660), next);
    }

    [Fact]
    public void BuildJoinUrl_HasTheExpectedShape()
    {
        string url = JoinTokenService.BuildJoinUrl("https://order.example.com/", Vector1Table, Vector1Window0Token);
        Assert.Equal(
            $"https://order.example.com/table/{Vector1Table:D}?token={Vector1Window0Token}",
            url);
    }

    [Theory]
    [InlineData(0)]   // current window
    [InlineData(-1)]  // immediately previous window
    public void Validate_AcceptsCurrentAndPreviousWindow(long windowDelta)
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        long window = JoinTokenService.CurrentWindowIndex(instant, 60) + windowDelta;
        string token = JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, window);

        Assert.Equal(
            JoinTokenValidationResult.Valid,
            JoinTokenService.Validate(Vector1Secret, Vector1Table, token, instant, 60));
    }

    [Theory]
    [InlineData(-2)]   // just past the accept window
    [InlineData(-11)]  // last window inside the default lookback
    public void Validate_ClassifiesRecentOlderWindowsAsExpired(long windowDelta)
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        long window = JoinTokenService.CurrentWindowIndex(instant, 60) + windowDelta;
        string token = JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, window);

        Assert.Equal(
            JoinTokenValidationResult.Expired,
            JoinTokenService.Validate(Vector1Secret, Vector1Table, token, instant, 60));
    }

    [Fact]
    public void Validate_ClassifiesTokensOlderThanLookbackAsInvalid()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        long window = JoinTokenService.CurrentWindowIndex(instant, 60) - 12; // one past the lookback
        string token = JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, window);

        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(Vector1Secret, Vector1Table, token, instant, 60));
    }

    [Fact]
    public void Validate_RejectsWrongSecret()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        long window = JoinTokenService.CurrentWindowIndex(instant, 60);
        string token = JoinTokenService.ComputeToken(Vector1Secret, Vector1Table, window);

        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(Vector2Secret, Vector1Table, token, instant, 60));
    }

    [Theory]
    [InlineData("this is not base64url!!")]
    [InlineData("")]
    public void Validate_RejectsMalformedTokens(string presented)
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);
        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(Vector1Secret, Vector1Table, presented, instant, 60));
    }

    [Fact]
    public void Validate_RejectsWellFormedButWrongLengthToken()
    {
        // Valid Base64Url, but decodes to 16 bytes rather than the 32-byte HMAC-SHA256 output.
        string sixteenBytes = Base64UrlText.Encode(new byte[16]);
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_784_289_600);

        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(Vector1Secret, Vector1Table, sixteenBytes, instant, 60));
    }

    private static byte[] CreateSequentialSecret()
    {
        byte[] secret = new byte[32];
        for (int index = 0; index < secret.Length; index++)
        {
            secret[index] = (byte)index;
        }

        return secret;
    }

    private static byte[] CreateRepeatedSecret(byte value, int length)
    {
        byte[] secret = new byte[length];
        Array.Fill(secret, value);
        return secret;
    }
}
