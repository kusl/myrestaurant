using Microsoft.AspNetCore.Http;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the WebAuthn origin-trust policy (TECHNICAL_SPECIFICATION §3.3, ADR-0005): which browser
/// origins may act as the relying party, and what host the app should present so the .NET 10 handler
/// derives the right RP ID. Pattern semantics mirror the reference GoTunnels matcher.
/// </summary>
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
    // The configured origin is always trusted (port must match when non-default).
    [InlineData("https://localhost:8443", true)]
    [InlineData("https://localhost:9999", true)]   // loopback, any port, is a secure context
    [InlineData("http://localhost:8080", true)]    // loopback over http is still a secure context
    [InlineData("http://127.0.0.1:5000", true)]
    // A quick-tunnel origin matches the default wildcard pattern.
    [InlineData("https://marie-editing-committed-preferred.trycloudflare.com", true)]
    [InlineData("https://bare-ministers-proceeds-prayer.trycloudflare.com", true)]
    // A deeper subdomain is NOT a single-label match, and a ported tunnel host does not match.
    [InlineData("https://a.b.trycloudflare.com", false)]
    [InlineData("https://foo.trycloudflare.com:8443", false)]
    // Untrusted or insecure origins are rejected.
    [InlineData("https://evil.example.com", false)]
    [InlineData("http://orders.example.com", false)] // non-loopback must be https
    [InlineData("https://trycloudflare.com", false)] // the bare apex is not a "*." label match
    [InlineData("", false)]
    [InlineData("not-an-origin", false)]
    [InlineData("https://foo.trycloudflare.com/evil", false)] // Origin values never carry a path
    public void IsTrustedOrigin_Dev(string origin, bool expected)
        => Assert.Equal(expected, Dev().IsTrustedOrigin(origin));

    [Theory]
    [InlineData("https://orders.example.com", true)]
    [InlineData("https://orders.example.com:443", true)] // explicit default port is equivalent
    [InlineData("https://marie-editing-committed-preferred.trycloudflare.com", true)] // pattern still applies
    [InlineData("https://evil.example.com", false)]
    public void IsTrustedOrigin_Prod(string origin, bool expected)
        => Assert.Equal(expected, Prod().IsTrustedOrigin(origin));

    [Theory]
    [InlineData("localhost:8443", true)]   // == configured public host
    [InlineData("localhost:8080", true)]   // dev: loopback trusted because the configured origin is loopback
    [InlineData("marie-editing-committed-preferred.trycloudflare.com", true)] // pattern host
    [InlineData("web:8080", false)]        // an internal service host is not public
    public void IsTrustedHost_Dev(string host, bool expected)
        => Assert.Equal(expected, Dev().IsTrustedHost(host));

    [Theory]
    [InlineData("orders.example.com", true)]
    [InlineData("foo.trycloudflare.com", true)]
    [InlineData("localhost:8080", false)]  // prod: a loopback request host is a proxy artifact, not public
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
