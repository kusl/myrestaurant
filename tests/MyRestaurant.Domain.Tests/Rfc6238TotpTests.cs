using System.Text;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class Rfc6238TotpTests
{
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
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "731029", now));
        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "050471", now));
    }

    [Fact]
    public void ValidateCode_RejectsTwoStepsEitherSide()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "150727", now));
        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now));
    }

    [Fact]
    public void ValidateCode_HonoursAWiderExplicitSkew()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        Assert.False(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now));
        Assert.True(Rfc6238Totp.ValidateCode(RfcSecret, "266759", now, allowedStepSkew: 2));
    }

    [Theory]
    [InlineData("81804")]
    [InlineData("0818040")]
    [InlineData("08180a")]
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
