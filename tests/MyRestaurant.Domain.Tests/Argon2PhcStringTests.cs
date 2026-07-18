using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Fixes the Argon2id PHC string format (TECHNICAL_SPECIFICATION §3.2): standard Base64 (not URL-safe),
/// no padding, canonical <c>$argon2id$v=19$m=..,t=..,p=..$salt$tag</c> shape. The all-zero salt/tag
/// encodings are hardcoded (independently computed) so the alphabet choice cannot silently regress.
/// </summary>
public sealed class Argon2PhcStringTests
{
    private const string ZeroSaltBase64 = "AAAAAAAAAAAAAAAAAAAAAA";                            // 16 zero bytes
    private const string ZeroTagBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";        // 32 zero bytes

    [Fact]
    public void Encode_ProducesTheCanonicalString()
    {
        Argon2Parameters parameters = new(65536, 3, 1, new byte[16], new byte[32]);

        string encoded = Argon2PhcString.Encode(parameters);

        Assert.Equal($"$argon2id$v=19$m=65536,t=3,p=1${ZeroSaltBase64}${ZeroTagBase64}", encoded);
    }

    [Fact]
    public void Parse_ReadsTheCanonicalString()
    {
        string phc = $"$argon2id$v=19$m=65536,t=3,p=1${ZeroSaltBase64}${ZeroTagBase64}";

        Argon2Parameters parameters = Argon2PhcString.Parse(phc);

        Assert.Equal(65536, parameters.MemoryKibibytes);
        Assert.Equal(3, parameters.Iterations);
        Assert.Equal(1, parameters.Parallelism);
        Assert.Equal(16, parameters.Salt.Length);
        Assert.Equal(32, parameters.Tag.Length);
        Assert.All(parameters.Salt, b => Assert.Equal(0, b));
        Assert.All(parameters.Tag, b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodeThenParse_RoundTripsArbitraryBytes()
    {
        byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] tag = new byte[32];
        for (int index = 0; index < tag.Length; index++)
        {
            tag[index] = (byte)(index * 7 + 3);
        }

        Argon2Parameters original = new(19456, 2, 1, salt, tag);

        Argon2Parameters roundTripped = Argon2PhcString.Parse(Argon2PhcString.Encode(original));

        Assert.Equal(original.MemoryKibibytes, roundTripped.MemoryKibibytes);
        Assert.Equal(original.Iterations, roundTripped.Iterations);
        Assert.Equal(original.Parallelism, roundTripped.Parallelism);
        Assert.Equal(salt, roundTripped.Salt);
        Assert.Equal(tag, roundTripped.Tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AAAA$AAAA")]     // wrong algorithm
    [InlineData("$argon2id$v=18$m=65536,t=3,p=1$AAAA$AAAA")]    // wrong version
    [InlineData("$argon2id$v=19$m=65536,t=3$AAAA$AAAA")]        // only two cost parameters
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$AAAA")]         // missing the tag segment
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$@@@@$AAAA")]    // salt is not valid base64
    public void TryParse_RejectsMalformedInput(string phc)
    {
        Assert.False(Argon2PhcString.TryParse(phc, out Argon2Parameters? parameters));
        Assert.Null(parameters);
    }

    [Fact]
    public void Parse_ThrowsOnMalformedInput()
        => Assert.Throws<FormatException>(() => Argon2PhcString.Parse("nonsense"));

    [Fact]
    public void NeedsRehash_IsFalseWhenParametersMatchConfiguration()
    {
        Argon2Parameters stored = new(65536, 3, 1, new byte[16], new byte[32]);
        Assert.False(Argon2PhcString.NeedsRehash(stored, 65536, 3, 1));
    }

    [Theory]
    [InlineData(131072, 3, 1)] // memory increased
    [InlineData(65536, 4, 1)]  // iterations increased
    [InlineData(65536, 3, 2)]  // parallelism changed
    public void NeedsRehash_IsTrueWhenAnyCostParameterDiffers(int memory, int iterations, int parallelism)
    {
        Argon2Parameters stored = new(65536, 3, 1, new byte[16], new byte[32]);
        Assert.True(Argon2PhcString.NeedsRehash(stored, memory, iterations, parallelism));
    }
}
