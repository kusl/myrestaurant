using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Security;

/// <summary>
/// Publishes <see cref="ResponseSecurityHeaders"/> on every response
/// (TECHNICAL_SPECIFICATION §11.11, ADR-0013, F-49).
///
/// <para><b>Before <c>next</c>, never after.</b> Headers can only be written while the response has not
/// started, and by the time an inner delegate returns, the body of a static file or a rendered page has
/// usually been flushed. Setting them on the way in is also what makes them survive a short circuit:
/// the rate limiter's 429, the obligations pipeline's redirect, <c>UseStaticFiles</c> answering without
/// calling anything further, and a 404 from the endpoint router all produce responses that this
/// middleware never sees a second time.</para>
///
/// <para><b>Where it sits, and why not first.</b> Immediately after <c>PublicOriginMiddleware</c>. The
/// policy's <c>connect-src</c> names the request's own host so the Blazor circuit's WebSocket is
/// admitted by origin rather than by scheme wildcard, and until that middleware has run,
/// <c>Request.Host</c> may still be the internal service address a tunnel left behind rather than the
/// host the browser is on. Nothing between the start of the pipeline and that point can produce a
/// response — <c>UseForwardedHeaders</c> and <c>PublicOriginMiddleware</c> both rewrite and call on —
/// so "after the host is trustworthy" and "before anything can answer" are the same position.</para>
///
/// <para><b>Assignment rather than append.</b> The framework's own
/// <c>frame-ancestors</c> convention appends to this header with <c>StringValues.Concat</c>, which
/// would deliver two policies on one response — both enforced, which is safe, and unreadable, which is
/// not. <c>Program.cs</c> turns that convention off; this uses the indexer so that anything else which
/// ever sets the header loses to the policy rather than joining it, and so that a re-entered pipeline
/// cannot double it.</para>
///
/// <para>Plain middleware rather than an endpoint convention or a filter, for the reason the
/// convention it replaces demonstrates: a convention reaches the endpoints it was attached to, and the
/// responses most worth a <c>nosniff</c> are the static ones that never reach an endpoint at all.</para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Apply(context.Response, context.Request.Host.Value);

        return _next(context);
    }

    /// <summary>
    /// Writes the three headers onto <paramref name="response"/> for a request that arrived on
    /// <paramref name="requestHost"/>. Separated from <see cref="InvokeAsync"/> so a test can assert
    /// the headers on a bare response without building a pipeline.
    /// </summary>
    internal static void Apply(HttpResponse response, string? requestHost)
    {
        ArgumentNullException.ThrowIfNull(response);

        IHeaderDictionary headers = response.Headers;
        headers[ResponseSecurityHeaders.ContentSecurityPolicyHeaderName] =
            ResponseSecurityHeaders.ContentSecurityPolicyFor(requestHost);
        headers[ResponseSecurityHeaders.ContentTypeOptionsHeaderName] =
            ResponseSecurityHeaders.ContentTypeOptions;
        headers[ResponseSecurityHeaders.ReferrerPolicyHeaderName] =
            ResponseSecurityHeaders.ReferrerPolicy;
    }
}
