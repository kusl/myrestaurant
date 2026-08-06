using System.Globalization;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

/// <summary>
/// The policy itself (TECHNICAL_SPECIFICATION §11.11, §16.4, F-49). Pure string arithmetic: no server,
/// no container, no clock.
///
/// <para>These assertions are about the <em>shape and strength</em> of the header this application
/// emits. Whether the tree it protects still fits inside it is a different question with a different
/// answer, and it lives in <see cref="ContentSecurityPolicyContractTests"/> — the two together are the
/// rule and the subject, kept apart on purpose so that neither can drift into being a restatement of
/// the other.</para>
/// </summary>
public sealed class ResponseSecurityHeadersTests
{
    /// <summary>A host with no port, as production presents behind a named tunnel.</summary>
    private const string PlainHost = "orders.example.com";

    /// <summary>A host with a port, as a bare `dotnet run` and the §16.3 harness both present.</summary>
    private const string HostWithPort = "localhost:5099";

    /// <summary>Every directive this policy is expected to carry, and nothing else.</summary>
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

    /// <summary>
    /// A duplicate directive is not an error the browser reports loudly — it silently keeps the first
    /// and ignores the rest, so the one somebody added last is the one that does nothing.
    /// </summary>
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

    /// <summary>
    /// A comma in this header value is not a syntax error: it splits one policy into two, and two
    /// policies are enforced as an intersection. A stray comma would therefore produce a header that
    /// looks approximately right and means something else, so it is asserted rather than assumed.
    /// Likewise a trailing semicolon, an empty directive, and a value with no source list.
    /// </summary>
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

    /// <summary>
    /// The directive that does the work. Microsoft's starter policy for a Blazor Web App carries
    /// <c>'wasm-unsafe-eval'</c> and a hash for the template's inline <c>onclick</c>; this tree has
    /// neither a WebAssembly render mode nor an inline handler, so it carries neither, and the contract
    /// test is what keeps that true as the markup changes.
    /// </summary>
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

    /// <summary>
    /// <c>'none'</c> rather than the framework's <c>'self'</c>, and on every response rather than on
    /// component endpoints. Nothing in this application frames anything, so self-framing is a
    /// capability with no user.
    /// </summary>
    [Fact]
    public void TheFramingDirectivesForbidEverySite()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'none'", directives["frame-ancestors"]);
        Assert.Equal("'none'", directives["frame-src"]);
    }

    /// <summary>
    /// The two concessions, each asserted so that removing the fact that earns one is a decision
    /// somebody makes here rather than a change nobody notices.
    /// </summary>
    [Fact]
    public void TheTwoConcessionsAreTheOnesRecorded()
    {
        IReadOnlyDictionary<string, string> directives = ParseDirectives(PlainHost);

        Assert.Equal("'self' 'unsafe-inline'", directives["style-src"]);
        Assert.Equal("'self' data:", directives["img-src"]);
    }

    /// <summary>
    /// The reason this policy takes an argument at all. <c>'self'</c> is an origin comparison and
    /// <c>wss://host</c> is not the same origin as <c>https://host</c>; CSP3's carve-out covers the
    /// secure pair and browsers have disagreed about the insecure one, which is the pair a bare
    /// `dotnet run` and the §16.3 harness both use. So both are named.
    /// </summary>
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

    /// <summary>
    /// CSP's <c>host-part</c> grammar has no way to write a bracketed address literal, so a directive
    /// that tried would be one the browser discards — which is worse than a looser one that works,
    /// because it fails as a blank screen with no cause named. The fallback is bounded to this
    /// directive and reachable only by pointing the public origin at an address literal on purpose.
    /// </summary>
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

    /// <summary>
    /// The two headers that are not the policy. Their values are pinned rather than described because
    /// each has exactly one correct spelling and a typo in either is silent.
    /// </summary>
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
