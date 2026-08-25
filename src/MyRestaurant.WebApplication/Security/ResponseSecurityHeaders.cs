namespace MyRestaurant.WebApplication.Security;

/// <summary>
/// The response security headers this application publishes on every response
/// (TECHNICAL_SPECIFICATION §11.11, ADR-0013, F-49). Pure: strings in, strings out, no HTTP types and
/// no framework types, so the policy itself is unit-testable without a server.
///
/// <para><b>Why the application publishes these and not the proxy.</b> This tree is deployed behind
/// Caddy in the dev profile, behind a Cloudflare tunnel in production, and behind nothing at all when
/// somebody runs <c>dotnet run</c> to reproduce a bug. Three fronting layers and one of them is a
/// third party's dashboard. A header that lives in `Caddyfile` is absent in two of those three, and a
/// header that lives at Cloudflare is absent from every fork that does not use Cloudflare. The same
/// argument F-45 made about the build-context guard applies here and for the same reason: the check
/// belongs where the risk is, and the risk travels with the application rather than with whatever
/// happens to sit in front of it this week.</para>
///
/// <para><b>What was already there.</b> Not nothing, and that is F-49's first half.
/// <c>AddInteractiveServerRenderMode</c> installs an endpoint convention that appends
/// <c>frame-ancestors 'self'</c> to <c>Content-Security-Policy</c> on every routable component
/// endpoint, because WebSocket compression plus framing is an attack and the framework will not ship
/// the first without mitigating the second. So this application has had a Content Security Policy
/// since M1, nobody wrote it, no document mentioned it, it covered exactly one directive, and it
/// covered only the endpoints that render components — not static files, not the health endpoints, not
/// the clock, not the sign-out POST. The framework appends with <c>StringValues.Concat</c>, so a
/// second policy set here would have been delivered <em>beside</em> it rather than instead of it;
/// <c>Program.cs</c> therefore sets <c>ContentSecurityFrameAncestorsPolicy</c> to <c>null</c> and this
/// type owns the whole header, which is the disposal the framework's own remarks prescribe — "care
/// must be taken to apply a policy in this case whenever the first document is rendered", and this one
/// is applied to every response of any kind.</para>
///
/// <para><b>The three headers, each with a threat.</b> Nothing is here for completeness.</para>
/// <list type="bullet">
///   <item><b><c>Content-Security-Policy</c></b> — the <c>MarkupString</c> sites (§3.4, §4.3, §4.5) are
///   the only raw HTML this application injects, and inline SVG can carry <c>&lt;script&gt;</c>. How
///   many there are is deliberately not written here: <c>RawHtmlContractTests</c> holds the set, and a
///   count beside an enforced copy of itself is the artefact F-112 and F-89 are about. Razor escapes
///   everything else, which is a reason for confidence and not a reason to skip the defence: the policy
///   is what makes an injection that gets past escaping non-executable rather than merely unlikely — and
///   it is only the <em>second</em> line, because markup this application serves from its own origin is
///   inside <c>'self'</c> and nothing in a policy distinguishes it from markup this application wrote.
///   It also carries <c>frame-ancestors 'none'</c> on every response instead of <c>'self'</c> on some,
///   and <c>form-action 'self'</c>, which is the one directive antiforgery does not cover — a token
///   protects against a forged request and says nothing about where a real form posts to.</item>
///   <item><b><c>X-Content-Type-Options: nosniff</c></b> — this application serves static files from
///   <c>wwwroot</c> and, in the container, published static web assets. A sniffed content type turns a
///   file the server called one thing into a script the browser treats as another.</item>
///   <item><b><c>Referrer-Policy: same-origin</c></b> — this one is specific to this product. §4.3's
///   join token travels in a <em>query string</em>: <c>/table/{id}?token=…</c>. Every current browser
///   defaults to <c>strict-origin-when-cross-origin</c>, which would not leak it, but a secret in a URL
///   protected by a browser default is protected by something no deployment here controls. Stating the
///   policy costs one header and moves the guarantee inside the program.</item>
/// </list>
///
/// <para><b>What is deliberately absent</b>, so that nobody adds it back without reading this:
/// <c>X-Frame-Options</c> (superseded by <c>frame-ancestors</c> in every browser that can run a Blazor
/// circuit, and a second spelling of one rule is a second thing to keep in step);
/// <c>Strict-Transport-Security</c> (an operator decision with a long memory — the wrong
/// <c>max-age</c> is not revocable from the application, and TLS is terminated at the edge, so the edge
/// is where it belongs: OPERATIONS §14); <c>Permissions-Policy</c> (a deny-list by construction, which
/// F-45 ruled against where a domain permits an allow-list — and here the two features this
/// application <em>does</em> use are screen wake lock and WebAuthn, so a wrong entry is a kitchen board
/// that sleeps mid-service, discovered by a cook rather than by a test);
/// <c>upgrade-insecure-requests</c> (there is not one absolute URL in the markup to upgrade); and any
/// reporting directive (there is no endpoint to report to, and a directive naming one that does not
/// exist is decoration).</para>
/// </summary>
public static class ResponseSecurityHeaders
{
    /// <summary>The policy header. Named here rather than taken from <c>HeaderNames</c> so this type stays BCL-only.</summary>
    public const string ContentSecurityPolicyHeaderName = "Content-Security-Policy";

    /// <summary>The MIME-sniffing header.</summary>
    public const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";

    /// <summary>The referrer header.</summary>
    public const string ReferrerPolicyHeaderName = "Referrer-Policy";

    /// <summary>The only value this header has ever had.</summary>
    public const string ContentTypeOptions = "nosniff";

    /// <summary>
    /// Full URL to our own pages, nothing at all to anybody else's. Stricter than every browser's
    /// current default because §4.3's token rides in a query string and a browser default is not a
    /// deployment guarantee.
    /// </summary>
    public const string ReferrerPolicy = "same-origin";

    /// <summary>
    /// The directives that do not depend on the request, in the order they are serialized.
    ///
    /// <para><c>default-src 'self'</c> rather than <c>'none'</c> is a decision, and it is the one place
    /// F-45's allow-list ruling is deliberately <em>not</em> applied. That ruling was about a set this
    /// project enumerates and controls — the paths in a build context. A CSP fallback governs a set the
    /// <em>browser</em> defines and extends with each new fetch destination, so <c>'none'</c> would be
    /// an allow-list over a vocabulary somebody else may add to, and the failure mode of guessing wrong
    /// is a screen in a working restaurant that stops showing something. <c>'self'</c> already denies
    /// every cross-origin origin, which is the threat; the directives below then take the ones that
    /// should be narrower down to <c>'none'</c> by name.</para>
    ///
    /// <para><c>style-src</c> carries <c>'unsafe-inline'</c> and that is a real concession rather than
    /// a shrug: twenty-one components carry a scoped <c>&lt;style&gt;</c> block, and Blazor's own
    /// reconnection overlay builds one at runtime with <c>innerHTML</c>, so without it the dialog a
    /// guest sees when a circuit drops would be unstyled. The concession is tied to the fact that earns
    /// it by a test — if those blocks ever move into <c>app.css</c>, the contract test fails and says
    /// to drop this.</para>
    ///
    /// <para><c>img-src</c> carries <c>data:</c> for exactly one thing: the empty <c>data:</c> favicon
    /// in <c>App.razor</c>, which exists to stop browsers requesting <c>/favicon.ico</c> on every page.
    /// A <c>&lt;link rel="icon"&gt;</c> is an image fetch as far as CSP is concerned, so without this
    /// every page load would log a violation. The contract test asserts that this is still the only
    /// <c>data:</c> URL in the tree.</para>
    ///
    /// <para><c>script-src 'self'</c> and nothing else — no <c>'unsafe-inline'</c>, no
    /// <c>'unsafe-eval'</c>, no <c>'wasm-unsafe-eval'</c>, no hash and no nonce. Every script in this
    /// application is a same-origin file, the framework's client is <c>_framework/blazor.web.js</c>,
    /// and there is no WebAssembly render mode here, so the keywords Microsoft's own starter policy
    /// carries for a Blazor Web App are keywords this tree does not need. The contract test asserts
    /// there is no inline script and no inline event-handler attribute anywhere in the markup, which is
    /// what keeps that true.</para>
    /// </summary>
    private static readonly string[] FixedDirectives =
    [
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        "frame-src 'none'",
        "frame-ancestors 'none'",
        "form-action 'self'",
        "img-src 'self' data:",
        "style-src 'self' 'unsafe-inline'",
        "script-src 'self'",
    ];

    /// <summary>
    /// The <c>connect-src</c> fallback used when the request's host cannot be written as a CSP
    /// <c>host-source</c> — see <see cref="ContentSecurityPolicyFor"/>.
    /// </summary>
    private const string SchemeOnlyWebSocketSources = "ws: wss:";

    /// <summary>
    /// The whole policy for a request arriving on <paramref name="requestHost"/> (the value of
    /// <c>HttpRequest.Host</c>, i.e. <c>host</c> or <c>host:port</c>, <b>after</b>
    /// <c>PublicOriginMiddleware</c> has normalized it to a trusted public host — which is why a value
    /// taken from the request can be written into a header without widening anything).
    ///
    /// <para><b>The reason <c>connect-src</c> is not simply <c>'self'</c> is the whole live-update
    /// layer of this application.</b> Every §9 notification arrives over the circuit's WebSocket, and
    /// CSP's <c>'self'</c> is an <em>origin</em> comparison: <c>wss://host</c> is not the same origin as
    /// <c>https://host</c>. CSP3 added an explicit carve-out so that <c>'self'</c> also matches the
    /// <c>https:</c> and <c>wss:</c> variants of the page's origin, and that covers production, where
    /// the page is https. It does not clearly cover <c>ws:</c> from an <c>http:</c> page, browsers have
    /// historically disagreed, and MDN still carries the warning — and <c>http</c> is exactly what a
    /// bare <c>dotnet run</c> serves and what the §16.3 harness boots. So the two WebSocket origins are
    /// named rather than inferred. This costs nothing in strictness: they are the page's own host,
    /// derived from the request that is being answered.</para>
    ///
    /// <para><b>The fallback.</b> CSP's <c>host-part</c> grammar is letters, digits, hyphens and dots —
    /// it cannot express a bracketed IPv6 literal at all, and a source expression the browser cannot
    /// parse is one it ignores. Rather than emit a directive that silently means less than it says,
    /// a host that cannot be written falls back to the bare <c>ws:</c> and <c>wss:</c> scheme sources.
    /// That is looser, it is bounded to one directive, and it is reachable only by pointing
    /// <c>RESTAURANT_PUBLIC_ORIGIN</c> at an address literal — a configuration somebody types on
    /// purpose, on a machine they are sitting at. The alternative was a circuit that refuses to open on
    /// <c>http://[::1]:8080</c> with no message that names the cause.</para>
    /// </summary>
    public static string ContentSecurityPolicyFor(string? requestHost)
        => string.Join("; ", FixedDirectives) + "; connect-src 'self' " + WebSocketSourcesFor(requestHost);

    /// <summary>
    /// The WebSocket source expressions for <paramref name="requestHost"/>: <c>ws://host wss://host</c>
    /// when the host is expressible as a CSP <c>host-source</c>, and <c>ws: wss:</c> when it is not.
    /// Exposed so a test can assert the two branches by name rather than by substring.
    /// </summary>
    public static string WebSocketSourcesFor(string? requestHost)
        => IsExpressibleAsHostSource(requestHost)
            ? "ws://" + requestHost + " wss://" + requestHost
            : SchemeOnlyWebSocketSources;

    /// <summary>
    /// Whether <paramref name="requestHost"/> can be written verbatim as a CSP <c>host-source</c>:
    /// dot-separated labels of letters, digits and hyphens, with an optional <c>:port</c> of digits.
    ///
    /// <para>Deliberately narrower than "is a valid host". A wildcard is not accepted, because nothing
    /// here should ever emit one; an empty label is not accepted, because <c>a..b</c> is not a host;
    /// and anything with a bracket, a colon that is not a port separator, a slash, a space or a
    /// non-ASCII character is refused rather than escaped, because a source expression is not a place
    /// to be clever. An internationalized domain reaches this method already Punycode-encoded by the
    /// URI machinery upstream, which is the form CSP requires anyway.</para>
    /// </summary>
    public static bool IsExpressibleAsHostSource(string? requestHost)
    {
        if (string.IsNullOrEmpty(requestHost))
        {
            return false;
        }

        string host = requestHost;
        int colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            string port = host[(colon + 1)..];
            if (port.Length == 0)
            {
                return false;
            }

            foreach (char character in port)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            host = host[..colon];
        }

        if (host.Length == 0)
        {
            return false;
        }

        int labelLength = 0;
        foreach (char character in host)
        {
            if (character == '.')
            {
                if (labelLength == 0)
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }

            labelLength++;
        }

        // A trailing dot is legal in the grammar but is never what this application is looking at, and
        // accepting it would mean two spellings of one host reaching the header. Refuse it.
        return labelLength > 0;
    }
}
