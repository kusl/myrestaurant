using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class Base64UrlTextTests
{
    [Fact]
    public void Encode_UsesUrlSafeAlphabetWithoutPadding()
    {
        string encoded = Base64UrlText.Encode([0xFB, 0xFF]);

        Assert.Equal("-_8", encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    public void EncodeThenDecode_RoundTripsEveryPaddingRemainder(int length)
    {
        byte[] original = new byte[length];
        for (int index = 0; index < length; index++)
        {
            original[index] = (byte)(index * 11 + 1);
        }

        string encoded = Base64UrlText.Encode(original);

        Assert.True(Base64UrlText.TryDecode(encoded, out byte[] decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryDecode_RejectsImpossibleLength()
    {
        Assert.False(Base64UrlText.TryDecode("A", out _));
    }

    [Fact]
    public void TryDecode_RejectsNull()
        => Assert.False(Base64UrlText.TryDecode(null!, out _));

    [Fact]
    public void TryDecode_RejectsIllegalCharacters()
        => Assert.False(Base64UrlText.TryDecode("****", out _));
}
