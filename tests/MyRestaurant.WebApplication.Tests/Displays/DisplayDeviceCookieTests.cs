using MyRestaurant.WebApplication.Displays;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

/// <summary>
/// Unit tests for <see cref="DisplayDeviceCookie"/> (TECHNICAL_SPECIFICATION §4.2). The cookie value is
/// specified verbatim — <c>device:{device_identifier}:{secret}</c> — and it is the only thing standing
/// between an anonymous request and a device principal, so the shape is pinned here rather than left to
/// the middleware to discover. Pure: no server, no container.
/// </summary>
public sealed class DisplayDeviceCookieTests
{
    private static readonly Guid DeviceIdentifier = Guid.Parse("0192f100-0000-7000-8000-0000000000d1");

    // 32 bytes as unpadded Base64Url — the shape SecretGenerator.GenerateBase64UrlSecret produces.
    private const string Secret = "Zm9vYmFyYmF6cXV4Zm9vYmFyYmF6cXV4Zm9vYmFyYmE";

    [Fact]
    public void Format_ProducesTheSpecifiedShape()
    {
        string value = DisplayDeviceCookie.Format(DeviceIdentifier, Secret);

        Assert.Equal($"device:{DeviceIdentifier:D}:{Secret}", value);
        Assert.StartsWith("device:", value, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RoundTripsWhatFormatWrote()
    {
        Assert.True(DisplayDeviceCookie.TryParse(
            DisplayDeviceCookie.Format(DeviceIdentifier, Secret), out DisplayDeviceCookieValue? parsed));

        Assert.NotNull(parsed);
        Assert.Equal(DeviceIdentifier, parsed!.DeviceIdentifier);

        // The secret is carried verbatim: it is hashed, never decoded, so a single altered character
        // must survive to the comparison rather than being normalised away here.
        Assert.Equal(Secret, parsed.Secret);
    }

    [Theory]
    [InlineData(null)]                                       // no cookie
    [InlineData("")]                                         // empty cookie
    [InlineData("nonsense")]                                 // no separators at all
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1")]  // secret missing
    [InlineData("0192f100-0000-7000-8000-0000000000d1:abc")]     // prefix missing
    [InlineData("Device:0192f100-0000-7000-8000-0000000000d1:abc")] // prefix is case-sensitive
    [InlineData("session:0192f100-0000-7000-8000-0000000000d1:abc")] // some other prefix
    [InlineData("device:not-a-guid:abc")]                    // unparseable identifier
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1:")]  // empty secret
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1:abc:extra")] // a fourth segment
    public void TryParse_RefusesAnythingThatIsNotTheSpecifiedShape(string? rawValue)
    {
        Assert.False(DisplayDeviceCookie.TryParse(rawValue, out DisplayDeviceCookieValue? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_RefusesTheAllZeroIdentifier()
    {
        // No UUIDv7 key can legitimately be Guid.Empty, and accepting it would send a pointless query.
        Assert.False(DisplayDeviceCookie.TryParse($"device:{Guid.Empty:D}:{Secret}", out DisplayDeviceCookieValue? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Lifetime_IsTheSpecifiedYear()
    {
        // §4.2: "expiry ~365 days". A display is paired once and then left alone for a season.
        Assert.Equal(TimeSpan.FromDays(365), DisplayDeviceCookie.Lifetime);
    }

    [Fact]
    public void Name_IsDistinctFromEveryOtherCookieTheApplicationSets()
    {
        // Distinct names keep the join grant, the setup ticket, and the authentication cookie from ever
        // being read as one another — the same discipline the Data-Protection purposes follow.
        Assert.Equal("myrestaurant.display", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.join", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.setup", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.authentication", DisplayDeviceCookie.Name);
    }
}
