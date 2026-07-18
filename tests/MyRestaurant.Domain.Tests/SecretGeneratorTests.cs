using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Confirms the sizes and basic randomness of the CSPRNG-backed secrets (TECHNICAL_SPECIFICATION
/// §4.1, §3.4, §4.2). The point is the contract (correct byte counts, non-repeating output), not
/// the entropy source, which is <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// </summary>
public sealed class SecretGeneratorTests
{
    [Fact]
    public void GenerateJoinSecret_Is32Bytes()
        => Assert.Equal(SecretGenerator.JoinSecretByteCount, SecretGenerator.GenerateJoinSecret().Length);

    [Fact]
    public void GenerateTotpSecret_Is20Bytes()
        => Assert.Equal(SecretGenerator.TotpSecretByteCount, SecretGenerator.GenerateTotpSecret().Length);

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public void GenerateBytes_ReturnsRequestedLength(int byteCount)
        => Assert.Equal(byteCount, SecretGenerator.GenerateBytes(byteCount).Length);

    [Fact]
    public void GenerateBytes_RejectsNonPositiveLength()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SecretGenerator.GenerateBytes(0));

    [Fact]
    public void GenerateJoinSecret_DoesNotRepeat()
        => Assert.NotEqual(SecretGenerator.GenerateJoinSecret(), SecretGenerator.GenerateJoinSecret());

    [Fact]
    public void GenerateBase64UrlSecret_DecodesBackToRequestedLength()
    {
        string secret = SecretGenerator.GenerateBase64UrlSecret(32);

        Assert.True(Base64UrlText.TryDecode(secret, out byte[] bytes));
        Assert.Equal(32, bytes.Length);
    }
}
