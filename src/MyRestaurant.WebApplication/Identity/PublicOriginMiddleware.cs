using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Normalizes <see cref="HttpRequest.Host"/> to the effective <b>public</b> host so the .NET 10
/// passkey handler — which derives the WebAuthn relying-party ID as
/// <c>IdentityPasskeyOptions.ServerDomain ?? Request.Host.Host</c> — always sees the host the browser
/// is actually on (TECHNICAL_SPECIFICATION §3.3, ADR-0005). This is what makes passkeys work behind a
/// Cloudflare <em>Quick Tunnel</em> whose <c>*.trycloudflare.com</c> hostname is random per run and
/// unknowable at startup, without pinning a boot-time origin.
///
/// <para>Runs immediately after <c>UseForwardedHeaders</c> (so it sees any proxy-supplied
/// <c>X-Forwarded-Host</c>) and before authentication and the ceremony endpoints. Resolution order:</para>
/// <list type="number">
///   <item>if the browser sent a single trusted <c>Origin</c> header, adopt its host — the
///   <c>Origin</c> header is set by the browser and cannot be forged by page script, so it is the
///   most reliable signal, and it makes the RP ID <em>self-healing</em> when a Quick Tunnel URL
///   rotates;</item>
///   <item>otherwise, if the incoming host is already a trusted public host (a real domain forwarded
///   via <c>X-Forwarded-Host</c>, or loopback in pure local development), keep it;</item>
///   <item>otherwise, fall back to the configured <c>RESTAURANT_PUBLIC_ORIGIN</c> host — this covers
///   requests that legitimately omit <c>Origin</c> (e.g. a top-level form POST on some browsers)
///   behind a tunnel that rewrote the host to the internal service address.</item>
/// </list>
///
/// <para>Every branch only ever sets the host to a value the <see cref="WebAuthnOriginPolicy"/>
/// already trusts, so this never widens what the app will answer for; it only corrects a host the
/// proxy layer left pointing at the internal service. See <see cref="WebAuthnOriginPolicy"/> for why
/// deriving the RP ID from the request is safe.</para>
/// </summary>
public sealed class PublicOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebAuthnOriginPolicy _policy;

    public PublicOriginMiddleware(RequestDelegate next, WebAuthnOriginPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(policy);
        _next = next;
        _policy = policy;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;

        if (request.Headers.TryGetValue(HeaderNames.Origin, out StringValues originValues)
            && originValues.Count == 1
            && _policy.TryResolveTrustedHost(originValues.ToString(), out HostString originHost))
        {
            // The browser told us its real origin, and we trust it — this wins over whatever the
            // proxy left in the Host header (which behind a Quick Tunnel is often the internal address).
            request.Host = originHost;
        }
        else if (!_policy.IsTrustedHost(request.Host.Value))
        {
            // No trusted Origin to go on, and the current host is not a public host we recognise, so
            // present the configured public origin's host instead of an internal/proxy value.
            request.Host = _policy.PublicHost;
        }

        return _next(context);
    }
}
