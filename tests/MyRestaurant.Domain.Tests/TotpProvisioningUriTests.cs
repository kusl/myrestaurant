using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class TotpProvisioningUriTests
{
    [Fact]
    public void Build_ProducesTheExpectedKeyUriFormat()
    {
        string uri = TotpProvisioningUri.Build("My Bistro", "casey", "GEZDGNBVGY3TQOJQ");

        Assert.Equal(
            "otpauth://totp/My%20Bistro:casey?secret=GEZDGNBVGY3TQOJQ&issuer=My%20Bistro",
            uri);
    }

    [Fact]
    public void Build_EscapesSeparatorsAndAmpersandsInComponents()
    {
        string uri = TotpProvisioningUri.Build("A&B: Grill", "user:name", "ABCDEF");

        Assert.Equal(
            "otpauth://totp/A%26B%3A%20Grill:user%3Aname?secret=ABCDEF&issuer=A%26B%3A%20Grill",
            uri);

        string query = uri[(uri.IndexOf('?', StringComparison.Ordinal) + 1)..];
        Assert.Equal(new[] { "secret=ABCDEF", "issuer=A%26B%3A%20Grill" }, query.Split('&'));
    }

    [Fact]
    public void Build_EscapesNonAsciiIssuer()
    {
        string uri = TotpProvisioningUri.Build("Café", "u", "ABCDEF");

        Assert.Contains("Caf%C3%A9:u", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Caf%C3%A9", uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "u", "S")]
    [InlineData("", "u", "S")]
    [InlineData("i", null, "S")]
    [InlineData("i", "", "S")]
    [InlineData("i", "u", null)]
    [InlineData("i", "u", "")]
    public void Build_RejectsMissingComponents(string? issuer, string? username, string? secret)
        => Assert.ThrowsAny<ArgumentException>(() => TotpProvisioningUri.Build(issuer!, username!, secret!));
}
