using System.Text;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Locks the RFC 6238 TOTP engine (TECHNICAL_SPECIFICATION §3.4) against the published Appendix B
/// vectors (6-digit truncation of the SHA-1 suite) and, crucially, the <b>±1 step</b> acceptance
/// window the spec mandates — one step either side accepted, two steps rejected. The RFC test secret
/// is the ASCII string "12345678901234567890" (exactly 20 bytes).
/// </summary>
public sealed class Rfc6238TotpTests
{
    // RFC 6238 Appendix B, SHA-1 shared secret.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void ComputeCode_MatchesRfc6238AppendixBVectors(long unixSeconds, string expected)
    {
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Assert.Equal(expected, Rfc6238Totp.ComputeCode(RfcSecret, timestamp));
    }

    [Fact]
    public void ComputeCode_IsAlwaysSixDigits()
    {
        // T=59 truncates to a value with a leading zero region in some suites; ensure zero-padding.
        string code = Rfc6238Totp.ComputeCode(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(59));

        Assert.Equal(6, code.Length);
        Assert.All(code, character => Assert.InRange(character, '0', '9'));
    }

    [Fact]
    public void ValidateCode_AcceptsTheCurrentStep()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "081804", now));
    }

    [Fact]
    public void ValidateCode_AcceptsOneStepEitherSide()
    {
        // Anchor unix 1111111109 → step 37037036 → "081804". One step earlier/later:
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "731029", now));  // step −1
        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "050471", now));  // step +1 (== T=1111111111)
    }

    [Fact]
    public void ValidateCode_RejectsTwoStepsEitherSide()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "150727", now)); // step −2
        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now)); // step +2
    }

    [Fact]
    public void ValidateCode_HonoursAWiderExplicitSkew()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        // The default rejects ±2, but an explicit window of 2 accepts it — guards the parameter.
        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now));
        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now, allowedStepSkew: 2));
    }

    [Theory]
    [InlineData("81804")]     // too short
    [InlineData("0818040")]   // too long
    [InlineData("08180a")]    // non-numeric
    [InlineData("")]
    public void ValidateCode_RejectsMalformedInput(string code)
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, code, now));
    }

    [Fact]
    public void ValidateCode_RejectsNull()
        => Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, null!, DateTimeOffset.FromUnixTimeSeconds(1111111109)));

    [Fact]
    public void ValidateCode_NegativeSkew_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Rfc6238Totp.ValidateCode(RfcSecret, "081804", DateTimeOffset.UtcNow, allowedStepSkew: -1));

    [Fact]
    public void GenerateSecret_IsTwentyBytes()
        => Assert.Equal(Rfc6238Totp.SecretSizeInBytes, Rfc6238Totp.GenerateSecret().Length);

    [Fact]
    public void GenerateSecret_IsRandom()
        => Assert.NotEqual(Rfc6238Totp.GenerateSecret(), Rfc6238Totp.GenerateSecret());
}
