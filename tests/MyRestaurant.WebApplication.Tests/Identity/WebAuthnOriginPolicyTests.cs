using Microsoft.AspNetCore.Http;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class WebAuthnOriginPolicyTests
{
    private static WebAuthnOriginPolicy Dev()
        => new("https://localhost:8443", RestaurantOptionsDefaults());

    private static WebAuthnOriginPolicy Prod()
        => new("https://orders.example.com", RestaurantOptionsDefaults());

    private static string[] RestaurantOptionsDefaults() => ["https://*.trycloudflare.com"];

    [Fact]
    public void PublicHost_KeepsNonDefaultPortAndDropsDefault()
    {
        Assert.Equal("localhost:8443", Dev().PublicHost.Value);
        Assert.Equal("orders.example.com", Prod().PublicHost.Value);
    }

    [Theory]

    [InlineData("https://localhost:8443", true)]
    [InlineData("https://localhost:9999", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:5000", true)]

    [InlineData("https://marie-editing-committed-preferred.trycloudflare.com", true)]
    [InlineData("https://bare-ministers-proceeds-prayer.trycloudflare.com", true)]

    [InlineData("https://a.b.trycloudflare.com", false)]
    [InlineData("https://foo.trycloudflare.com:8443", false)]

    [InlineData("https://evil.example.com", false)]
    [InlineData("http://orders.example.com", false)]
    [InlineData("https://trycloudflare.com", false)]
    [InlineData("", false)]
    [InlineData("not-an-origin", false)]
    [InlineData("https://foo.trycloudflare.com/evil", false)]
    public void IsTrustedOrigin_Dev(string origin, bool expected)
        => Assert.Equal(expected, Dev().IsTrustedOrigin(origin));

    [Theory]
    [InlineData("https://orders.example.com", true)]
    [InlineData("https://orders.example.com:443", true)]
    [InlineData("https://marie-editing-committed-preferred.trycloudflare.com", true)]
    [InlineData("https://evil.example.com", false)]
    public void IsTrustedOrigin_Prod(string origin, bool expected)
        => Assert.Equal(expected, Prod().IsTrustedOrigin(origin));

    [Theory]
    [InlineData("localhost:8443", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("marie-editing-committed-preferred.trycloudflare.com", true)]
    [InlineData("web:8080", false)]
    public void IsTrustedHost_Dev(string host, bool expected)
        => Assert.Equal(expected, Dev().IsTrustedHost(host));

    [Theory]
    [InlineData("orders.example.com", true)]
    [InlineData("foo.trycloudflare.com", true)]
    [InlineData("localhost:8080", false)]
    [InlineData("web:8080", false)]
    public void IsTrustedHost_Prod(string host, bool expected)
        => Assert.Equal(expected, Prod().IsTrustedHost(host));

    [Fact]
    public void TryResolveTrustedHost_ReturnsHostForTrustedOrigin_AndDropsDefaultPort()
    {
        Assert.True(Dev().TryResolveTrustedHost("https://foo.trycloudflare.com", out HostString host));
        Assert.Equal("foo.trycloudflare.com", host.Value);

        Assert.True(Dev().TryResolveTrustedHost("https://localhost:8443", out HostString ported));
        Assert.Equal("localhost:8443", ported.Value);
    }

    [Fact]
    public void TryResolveTrustedHost_RejectsUntrustedOrigin()
    {
        Assert.False(Dev().TryResolveTrustedHost("https://evil.example.com", out HostString host));
        Assert.False(host.HasValue);
    }

    [Fact]
    public void EmptyPatternList_StillTrustsConfiguredOriginAndLoopback_ButNotTunnels()
    {
        WebAuthnOriginPolicy noPatterns = new("https://orders.example.com", []);

        Assert.True(noPatterns.IsTrustedOrigin("https://orders.example.com"));
        Assert.True(noPatterns.IsTrustedOrigin("http://localhost:8080"));
        Assert.False(noPatterns.IsTrustedOrigin("https://foo.trycloudflare.com"));
    }
}
