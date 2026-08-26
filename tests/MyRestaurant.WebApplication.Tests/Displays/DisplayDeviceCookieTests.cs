using MyRestaurant.WebApplication.Displays;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

public sealed class DisplayDeviceCookieTests
{
    private static readonly Guid DeviceIdentifier = Guid.Parse("0192f100-0000-7000-8000-0000000000d1");

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

        Assert.Equal(Secret, parsed.Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1")]
    [InlineData("0192f100-0000-7000-8000-0000000000d1:abc")]
    [InlineData("Device:0192f100-0000-7000-8000-0000000000d1:abc")]
    [InlineData("session:0192f100-0000-7000-8000-0000000000d1:abc")]
    [InlineData("device:not-a-guid:abc")]
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1:")]
    [InlineData("device:0192f100-0000-7000-8000-0000000000d1:abc:extra")]
    public void TryParse_RefusesAnythingThatIsNotTheSpecifiedShape(string? rawValue)
    {
        Assert.False(DisplayDeviceCookie.TryParse(rawValue, out DisplayDeviceCookieValue? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_RefusesTheAllZeroIdentifier()
    {
        Assert.False(DisplayDeviceCookie.TryParse($"device:{Guid.Empty:D}:{Secret}", out DisplayDeviceCookieValue? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Lifetime_IsTheSpecifiedYear()
    {
        Assert.Equal(TimeSpan.FromDays(365), DisplayDeviceCookie.Lifetime);
    }

    [Fact]
    public void Name_IsDistinctFromEveryOtherCookieTheApplicationSets()
    {
        Assert.Equal("myrestaurant.display", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.join", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.setup", DisplayDeviceCookie.Name);
        Assert.NotEqual("myrestaurant.authentication", DisplayDeviceCookie.Name);
    }
}
