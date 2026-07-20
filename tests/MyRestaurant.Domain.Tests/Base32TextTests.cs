using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Locks the Base32 encoding (RFC 4648 §6, uppercase, no padding) used for the TOTP secret in the
/// provisioning URI and the authenticator key (TECHNICAL_SPECIFICATION §3.4). Encoding must match the
/// published vectors byte-for-byte; decoding must be forgiving of case and grouped-display
/// separators but reject impossible lengths, nonzero leftover bits, and illegal characters without
/// throwing.
/// </summary>
public sealed class Base32TextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encode_MatchesRfc4648Vectors(string ascii, string expected)
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes(ascii);

        Assert.Equal(expected, Base32Text.Encode(input));
    }

    [Fact]
    public void Encode_TwentyByteSecret_IsThirtyTwoCharactersNoPadding()
    {
        // Bytes 0x01..0x14 — a 20-byte secret encodes to exactly 32 Base32 characters, no '='.
        byte[] secret = new byte[20];
        for (int index = 0; index < secret.Length; index++)
        {
            secret[index] = (byte)(index + 1);
        }

        string encoded = Base32Text.Encode(secret);

        Assert.Equal("AEBAGBAFAYDQQCIKBMGA2DQPCAIREEYU", encoded);
        Assert.Equal(32, encoded.Length);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(32)]
    public void EncodeThenDecode_RoundTripsEveryLength(int length)
    {
        byte[] original = new byte[length];
        for (int index = 0; index < length; index++)
        {
            original[index] = (byte)(index * 7 + 3);
        }

        string encoded = Base32Text.Encode(original);

        Assert.True(Base32Text.TryDecode(encoded, out byte[] decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryDecode_IsCaseInsensitive()
    {
        Assert.True(Base32Text.TryDecode("mzxw6ytboi", out byte[] lower));
        Assert.Equal("foobar", System.Text.Encoding.ASCII.GetString(lower));
    }

    [Fact]
    public void TryDecode_IgnoresGroupingSeparators()
    {
        // The manual-entry display groups the key with spaces; a user might type dashes too.
        Assert.True(Base32Text.TryDecode("MZXW 6YTB-OI", out byte[] spaced));
        Assert.Equal("foobar", System.Text.Encoding.ASCII.GetString(spaced));
    }

    [Fact]
    public void TryDecode_AcceptsTrailingPadding()
    {
        Assert.True(Base32Text.TryDecode("MY======", out byte[] padded));
        Assert.Equal("f", System.Text.Encoding.ASCII.GetString(padded));
    }

    [Theory]
    [InlineData("A")]      // 5 leftover bits — impossible length
    [InlineData("ABC")]    // 15 bits → 1 byte + 7 leftover — impossible
    [InlineData("ABCDEF")] // 30 bits → 3 bytes + 6 leftover — impossible
    public void TryDecode_RejectsImpossibleLengths(string text)
        => Assert.False(Base32Text.TryDecode(text, out _));

    [Fact]
    public void TryDecode_RejectsNonZeroLeftoverBits()
    {
        // "MZ" decodes the first byte then leaves nonzero trailing bits — a corrupted tail, not "f".
        Assert.False(Base32Text.TryDecode("MZ", out _));
    }

    [Theory]
    [InlineData("MZXW6YTB0I")] // '0' is not in the RFC alphabet
    [InlineData("MZXW6YTB1I")] // '1' is not in the RFC alphabet
    [InlineData("MZXW6=TBOI")] // '=' anywhere but the trailing run
    [InlineData("****")]
    public void TryDecode_RejectsIllegalCharacters(string text)
        => Assert.False(Base32Text.TryDecode(text, out _));

    [Fact]
    public void TryDecode_RejectsNull()
        => Assert.False(Base32Text.TryDecode(null!, out _));
}
