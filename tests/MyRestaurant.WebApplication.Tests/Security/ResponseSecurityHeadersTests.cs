using System.Globalization;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

public sealed class ResponseSecurityHeadersTests
{
    private const string PlainHost = "orders.example.com";

    private const string HostWithPort = "localhost:5099";

    private static readonly IReadOnlyList<string> ExpectedDirectiveNames =
    [
        "default-src",
        "base-uri",
        "object-src",
        "frame-src",
        "frame-ancestors",
        "form-action",
        "img-src",
        "style-src",
        "script-src",
        "connect-src",
    ];

    [Fact]
    public void ThePolicyCarriesEveryExpectedDirectiveExactlyOnce()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal(
            ExpectedDirectiveNames.Order(StringComparer.Ordinal),
            directives.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePolicyRepeatsNoDirectiveName()
    {
        string policy = ResponseSecurityHeaders.ContentSecurityPolicyFor(PlainHost);

        List<string> names = [];
        foreach (string token in policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            names.Add(token.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ThePolicyIsOnePolicyAndIsWellFormed()
    {
        string policy = ResponseSecurityHeaders.ContentSecurityPolicyFor(PlainHost);

        Assert.DoesNotContain(',', policy);
        Assert.DoesNotContain("  ", policy, StringComparison.Ordinal);
        Assert.DoesNotContain(";;", policy, StringComparison.Ordinal);
        Assert.False(policy.EndsWith(';'), "the policy ends with a semicolon and an empty directive after it");
        Assert.Equal(policy.Trim(), policy);

        foreach (string token in policy.Split(';'))
        {
            string directive = token.Trim();
            Assert.NotEqual(string.Empty, directive);
            Assert.Contains(' ', directive);
        }
    }

    [Fact]
    public void TheScriptDirectiveAdmitsSameOriginFilesAndNothingElse()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'self'", directives["script-src"]);
    }

    [Fact]
    public void NoDirectiveAdmitsAWildcardOrDynamicCode()
    {
        string policy = ResponseSecurityHeaders.ContentSecurityPolicyFor(PlainHost);

        Assert.DoesNotContain('*', policy);
        Assert.DoesNotContain("'unsafe-eval'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("'wasm-unsafe-eval'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("'strict-dynamic'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-hashes'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFramingDirectivesForbidEverySite()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'none'", directives["frame-ancestors"]);
        Assert.Equal("'none'", directives["frame-src"]);
    }

    [Fact]
    public void TheTwoConcessionsAreTheOnesRecorded()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'self' 'unsafe-inline'", directives["style-src"]);
        Assert.Equal("'self' data:", directives["img-src"]);
    }

    [Fact]
    public void TheWebSocketSourcesNameTheRequestsOwnHost()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'self' ws://orders.example.com wss://orders.example.com", directives["connect-src"]);
    }

    [Fact]
    public void TheWebSocketSourcesKeepTheRequestsPort()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(HostWithPort);

        Assert.Equal("'self' ws://localhost:5099 wss://localhost:5099", directives["connect-src"]);
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("[::1]:8080")]
    [InlineData("[2001:db8::1]:443")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("orders.example.com:")]
    [InlineData("orders.example.com:80a")]
    [InlineData("orders..example.com")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("orders example.com")]
    [InlineData("*.example.com")]
    [InlineData("orders.example.com/path")]
    public void AHostThatCannotBeWrittenAsAHostSourceFallsBackToTheSchemes(string? host)
    {
        Assert.False(ResponseSecurityHeaders.IsExpressibleAsHostSource(host));
        Assert.Equal("ws: wss:", ResponseSecurityHeaders.WebSocketSourcesFor(host));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    [InlineData("127.0.0.1:5099")]
    [InlineData("orders.example.com")]
    [InlineData("marie-editing-committed-preferred.trycloudflare.com")]
    [InlineData("xn--tdaaaaaa.de")]
    public void AHostThatCanBeWrittenIsWritten(string host)
    {
        Assert.True(ResponseSecurityHeaders.IsExpressibleAsHostSource(host));
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"ws://{host} wss://{host}"),
            ResponseSecurityHeaders.WebSocketSourcesFor(host));
    }

    [Fact]
    public void TheOtherTwoHeadersHaveTheValuesSeventeenNames()
    {
        Assert.Equal("nosniff", ResponseSecurityHeaders.ContentTypeOptions);
        Assert.Equal("same-origin", ResponseSecurityHeaders.ReferrerPolicy);
    }

    private static IReadOnlyDictionary<string, string> ParseDirectives(string? host)
    {
        Dictionary<string, string> directives = new(StringComparer.Ordinal);

        foreach (string token in ResponseSecurityHeaders
            .ContentSecurityPolicyFor(host)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = token.IndexOf(' ', StringComparison.Ordinal);
            Assert.True(space > 0, $"'{token}' is a directive with no source list.");
            directives[token[..space]] = token[(space + 1)..];
        }

        return directives;
    }
}
