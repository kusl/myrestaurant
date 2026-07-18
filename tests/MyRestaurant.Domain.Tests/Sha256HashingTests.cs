using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Confirms SHA-256 hashing of stored secrets (TECHNICAL_SPECIFICATION §3.4, §4.2) against a known
/// digest, and that comparison against a stored hash is by value (constant-time under the hood).
/// </summary>
public sealed class Sha256HashingTests
{
    [Fact]
    public void Hash_MatchesKnownDigest()
    {
        byte[] hash = Sha256Hashing.Hash("myrestaurant");

        Assert.Equal(
            "7dcb0757f44ed532149b7b722e7017b3407266567f4496cc4ea313a2ce4eafb9",
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Fact]
    public void HashByteCount_Is32()
        => Assert.Equal(32, Sha256Hashing.HashByteCount);

    [Fact]
    public void MatchesStoredHash_TrueForTheSameValue()
    {
        byte[] stored = Sha256Hashing.Hash("PAIR-CODE-XYZ");
        Assert.True(Sha256Hashing.MatchesStoredHash("PAIR-CODE-XYZ", stored));
    }

    [Fact]
    public void MatchesStoredHash_FalseForADifferentValue()
    {
        byte[] stored = Sha256Hashing.Hash("PAIR-CODE-XYZ");
        Assert.False(Sha256Hashing.MatchesStoredHash("PAIR-CODE-ZZZ", stored));
    }
}
