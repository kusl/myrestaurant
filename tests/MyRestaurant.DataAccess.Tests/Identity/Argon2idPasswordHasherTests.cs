using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Unit tests for the Argon2id <see cref="IPasswordHasher{TUser}"/> (TECHNICAL_SPECIFICATION §3.2,
/// ADR-0008). These compute real Argon2, so they need no container — but they do use modest cost
/// parameters (still above the startup floor) to stay fast. They assert the round-trip, rejection
/// of wrong/garbage input, PHC-string shape, salt randomness, and the transparent-rehash signal.
/// </summary>
public sealed class Argon2idPasswordHasherTests
{
    // Above the §3.2 floor (19456/2/1) yet quick enough for a unit test.
    private static readonly Argon2HashingOptions FastOptions = new(
        MemoryKibibytes: 19456, Iterations: 2, Parallelism: 1, MaxConcurrentHashes: 4);

    private static readonly Person AnyUser = new() { Username = "hash.subject" };

    private static Argon2idPasswordHasher NewHasher(Argon2HashingOptions? options = null)
        => new(options ?? FastOptions);

    [Fact]
    public void HashThenVerify_WithCorrectPassword_Succeeds()
    {
        using Argon2idPasswordHasher hasher = NewHasher();
        string hash = hasher.HashPassword(AnyUser, "correct horse battery staple");

        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(AnyUser, hash, "correct horse battery staple"));
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        using Argon2idPasswordHasher hasher = NewHasher();
        string hash = hasher.HashPassword(AnyUser, "correct horse battery staple");

        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(AnyUser, hash, "Correct Horse Battery Staple"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2id$v=19$m=notanumber$c2FsdA$dGFn")]
    public void Verify_WithMalformedOrEmptyStoredHash_Fails(string storedHash)
    {
        using Argon2idPasswordHasher hasher = NewHasher();

        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(AnyUser, storedHash, "any password at all"));
    }

    [Fact]
    public void Hash_ProducesAWellFormedPhcStringCarryingTheConfiguredParameters()
    {
        using Argon2idPasswordHasher hasher = NewHasher();
        string hash = hasher.HashPassword(AnyUser, "another strong passphrase");

        Assert.True(Argon2PhcString.TryParse(hash, out Argon2Parameters? parameters));
        Assert.Equal(FastOptions.MemoryKibibytes, parameters!.MemoryKibibytes);
        Assert.Equal(FastOptions.Iterations, parameters.Iterations);
        Assert.Equal(FastOptions.Parallelism, parameters.Parallelism);
        Assert.Equal(Argon2idPasswordHasher.SaltByteCount, parameters.Salt.Length);
        Assert.Equal(Argon2idPasswordHasher.TagByteCount, parameters.Tag.Length);
    }

    [Fact]
    public void Hash_IsSaltedSoTwoHashesOfTheSamePasswordDiffer()
    {
        using Argon2idPasswordHasher hasher = NewHasher();

        string first = hasher.HashPassword(AnyUser, "identical passphrase value");
        string second = hasher.HashPassword(AnyUser, "identical passphrase value");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_AgainstAHashMadeWithDifferentParameters_SignalsRehashNeeded()
    {
        // A hash produced with weaker (still-valid) parameters must verify but ask to be rehashed
        // when the current configuration is stronger — Identity then rehashes at sign-in.
        Argon2HashingOptions weaker = FastOptions with { Iterations = FastOptions.Iterations + 1 };
        using Argon2idPasswordHasher legacyHasher = NewHasher(weaker);
        using Argon2idPasswordHasher currentHasher = NewHasher(FastOptions);

        string legacyHash = legacyHasher.HashPassword(AnyUser, "rehash me please");

        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            currentHasher.VerifyHashedPassword(AnyUser, legacyHash, "rehash me please"));
    }
}
