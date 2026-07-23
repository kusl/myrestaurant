using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The origin-trust policy for WebAuthn (TECHNICAL_SPECIFICATION §3.3, ADR-0005). It answers two
/// questions the passkey ceremony depends on, purely from configuration (no HTTP, no framework
/// types beyond <see cref="HostString"/>), so it is unit-testable in isolation:
/// <list type="number">
///   <item><b>Which browser origins may act as the relying party?</b> — the configured
///   <c>RESTAURANT_PUBLIC_ORIGIN</c>, any origin matching a trusted wildcard pattern
///   (<c>RESTAURANT_TRUSTED_ORIGIN_PATTERNS</c>, default <c>https://*.trycloudflare.com</c>), and
///   loopback in development. This is what <see cref="Microsoft.AspNetCore.Identity.IdentityPasskeyOptions.ValidateOrigin"/>
///   consults against the browser's cryptographically-signed <c>clientDataJSON.origin</c>.</item>
///   <item><b>What host should the app present as <see cref="HttpRequest.Host"/></b> so the .NET 10
///   passkey handler — which derives the RP ID as <c>options.ServerDomain ?? Request.Host.Host</c> —
///   produces the origin's own host? <see cref="PublicHost"/> is the configured fallback and
///   <see cref="TryResolveTrustedHost"/> turns a trusted origin into its host. <see cref="PublicOriginMiddleware"/>
///   uses both.</item>
/// </list>
///
/// <para><b>Why this is safe (the ADR-0005 course correction).</b> Deriving the RP ID from the
/// request rather than pinning it to a boot-time origin is what lets passkeys work behind a
/// Cloudflare <em>Quick Tunnel</em>, whose <c>*.trycloudflare.com</c> hostname is random per run and
/// unknowable at startup. It cannot be abused to steal credentials: WebAuthn credentials are scoped
/// to the RP ID by the authenticator itself, so a page on <c>attacker.trycloudflare.com</c> can only
/// ever exercise credentials created for <c>attacker.trycloudflare.com</c> — never one registered for
/// your instance. The trusted-origin gate here is defence in depth, not the primary control. (The
/// same is deliberately <em>not</em> true of CORS-with-credentials — but this app is single-origin
/// and sets no cross-origin CORS allowance, so that risk does not arise. See ADR-0005.)</para>
///
/// <para>Pattern semantics mirror the reference GoTunnels implementation exactly: a pattern is
/// <c>scheme://host</c> with no path/query/port; a leading <c>*.</c> in the host matches exactly one
/// non-empty DNS label (so <c>https://*.trycloudflare.com</c> matches
/// <c>https://marie-editing-committed-preferred.trycloudflare.com</c> but not a deeper
/// <c>https://a.b.trycloudflare.com</c>, and never a ported host).</para>
/// </summary>
public sealed class WebAuthnOriginPolicy
{
    private readonly string _publicOriginHostPort; // lowercased "host[:port]" of the configured origin
    private readonly bool _publicOriginIsLoopback;
    private readonly IReadOnlyList<(string Scheme, string Host)> _patterns;

    /// <summary>
    /// Builds the policy from the configured public origin (already validated as an absolute https
    /// URL by <see cref="Configuration.RestaurantOptions.Validate"/>) and the trusted origin patterns.
    /// </summary>
    public WebAuthnOriginPolicy(string publicOrigin, IEnumerable<string> trustedOriginPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);
        ArgumentNullException.ThrowIfNull(trustedOriginPatterns);

        Uri origin = new(publicOrigin, UriKind.Absolute);
        PublicHost = origin.IsDefaultPort ? new HostString(origin.Host) : new HostString(origin.Host, origin.Port);
        _publicOriginHostPort = PublicHost.Value.ToLowerInvariant();
        _publicOriginIsLoopback = IsLoopbackHost(origin.Host);

        List<(string, string)> parsed = [];
        foreach (string pattern in trustedOriginPatterns)
        {
            if (TrySplitOrigin(pattern, out string scheme, out string host))
            {
                parsed.Add((scheme, host));
            }
        }

        _patterns = parsed;
    }

    /// <summary>
    /// The host (with a non-default port only) of the configured <c>RESTAURANT_PUBLIC_ORIGIN</c>.
    /// <see cref="PublicOriginMiddleware"/> falls back to this when the incoming request carries no
    /// trusted <c>Origin</c> header and its host is not already a trusted public host.
    /// </summary>
    public HostString PublicHost { get; }

    /// <summary>
    /// Whether <paramref name="origin"/> (a full <c>scheme://host</c>, e.g. the signed
    /// <c>clientDataJSON.origin</c>) is trusted to act as the WebAuthn relying party.
    /// </summary>
    public bool IsTrustedOrigin(string? origin)
    {
        if (!TrySplitOrigin(origin, out string scheme, out string host))
        {
            return false;
        }

        // Loopback is a secure context in every browser regardless of scheme, so allow it for local
        // development (a bare `dotnet run` serves http://localhost:8080 as well as https://localhost:8443).
        if (IsLoopbackHost(HostOnly(host)))
        {
            return scheme is "https" or "http";
        }

        // Everything non-loopback must be https (WebAuthn requires a secure context).
        if (scheme != "https")
        {
            return false;
        }

        if (host == _publicOriginHostPort)
        {
            return true;
        }

        foreach ((string patternScheme, string patternHost) in _patterns)
        {
            if (scheme == patternScheme && HostMatches(patternHost, host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a bare request host (<see cref="HttpRequest.Host"/>'s <c>host[:port]</c> value, which
    /// has no scheme) is already a trusted public host and can be kept as-is. Loopback counts only
    /// when the configured public origin is itself loopback — behind a tunnel a loopback request host
    /// is an artifact of the proxy, not the real public host, and must be replaced (see the middleware).
    /// </summary>
    public bool IsTrustedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string value = host.Trim().ToLowerInvariant();
        if (value == _publicOriginHostPort)
        {
            return true;
        }

        if (_publicOriginIsLoopback && IsLoopbackHost(HostOnly(value)))
        {
            return true;
        }

        foreach ((_, string patternHost) in _patterns)
        {
            if (HostMatches(patternHost, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// If <paramref name="origin"/> is a trusted origin, produces the <see cref="HostString"/> the app
    /// should present as <see cref="HttpRequest.Host"/> (its host, plus a non-default port only).
    /// </summary>
    public bool TryResolveTrustedHost(string? origin, out HostString host)
    {
        host = default;
        if (!IsTrustedOrigin(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        host = uri.IsDefaultPort ? new HostString(uri.Host) : new HostString(uri.Host, uri.Port);
        return true;
    }

    // --- pattern / origin helpers (mirroring GoTunnels internal/httpx MatchOriginPattern) -----------

    /// <summary>
    /// Matches a candidate <c>host[:port]</c> against a pattern host. A leading <c>*.</c> matches
    /// exactly one non-empty label containing none of <c>. : /</c>; otherwise the match is exact.
    /// </summary>
    private static bool HostMatches(string patternHost, string candidateHost)
    {
        if (!patternHost.StartsWith("*.", StringComparison.Ordinal))
        {
            return patternHost == candidateHost;
        }

        string suffix = patternHost[1..]; // ".trycloudflare.com"
        if (!candidateHost.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string label = candidateHost[..^suffix.Length];
        return label.Length > 0 && !label.AsSpan().ContainsAny('.', ':', '/');
    }

    /// <summary>
    /// Lowercases and splits <c>scheme://host</c>. The host keeps any port verbatim. Anything with a
    /// path, query, fragment, userinfo, or whitespace is rejected — Origin values never carry those.
    /// </summary>
    private static bool TrySplitOrigin(string? origin, out string scheme, out string host)
    {
        scheme = string.Empty;
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        string value = origin.Trim().ToLowerInvariant();
        int marker = value.IndexOf("://", StringComparison.Ordinal);
        if (marker <= 0)
        {
            return false;
        }

        scheme = value[..marker];
        host = value[(marker + 3)..];
        if (host.Length == 0 || host.AsSpan().ContainsAny("/?#@ "))
        {
            scheme = string.Empty;
            host = string.Empty;
            return false;
        }

        return true;
    }

    private static string HostOnly(string hostPort)
    {
        // Strip a trailing :port. IPv6 literals are bracketed ("[::1]"), so only cut a colon that is
        // after the closing bracket (or when there is no bracket at all).
        int close = hostPort.LastIndexOf(']');
        int colon = hostPort.LastIndexOf(':');
        if (colon > close)
        {
            return hostPort[..colon];
        }

        return hostPort;
    }

    private static bool IsLoopbackHost(string host)
    {
        string bare = host.Trim('[', ']');
        return bare is "localhost" or "127.0.0.1" or "::1"
            || bare.EndsWith(".localhost", StringComparison.Ordinal);
    }
}
