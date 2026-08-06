using Microsoft.AspNetCore.Http;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

/// <summary>
/// The middleware that delivers the policy (TECHNICAL_SPECIFICATION §11.11, §16.4, F-49).
///
/// <para>The interesting assertions here are about <em>when</em> rather than <em>what</em>. A header
/// written after the inner pipeline returns is a header that arrives after the body has been flushed —
/// which is to say, not at all — and a short-circuited response is the case where that is easiest to
/// get wrong and hardest to notice, because the page that made you look is always the one that went all
/// the way through.</para>
///
/// <para>A real <see cref="DefaultHttpContext"/> and a real <see cref="RequestDelegate"/>: this runs the
/// middleware rather than describing it. No host, no server, no DI.</para>
/// </summary>
public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task EveryResponseCarriesTheThreeHeaders()
    {
        DefaultHttpContext context = ContextFor("orders.example.com");
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            ResponseSecurityHeaders.ContentSecurityPolicyFor("orders.example.com"),
            context.Response.Headers[ResponseSecurityHeaders.ContentSecurityPolicyHeaderName].ToString());
        Assert.Equal(
            ResponseSecurityHeaders.ContentTypeOptions,
            context.Response.Headers[ResponseSecurityHeaders.ContentTypeOptionsHeaderName].ToString());
        Assert.Equal(
            ResponseSecurityHeaders.ReferrerPolicy,
            context.Response.Headers[ResponseSecurityHeaders.ReferrerPolicyHeaderName].ToString());
    }

    /// <summary>
    /// The whole reason the headers are written on the way in. If this ever becomes an
    /// <c>await next(); …set headers…</c> the assertion below fails while every page still looks right
    /// in a browser, because a page that renders to completion has a response that started long before.
    /// </summary>
    [Fact]
    public async Task TheHeadersAreOnTheResponseBeforeTheRestOfThePipelineRuns()
    {
        bool sawThemFromInside = false;
        DefaultHttpContext context = ContextFor("orders.example.com");
        SecurityHeadersMiddleware middleware = new(inner =>
        {
            sawThemFromInside =
                inner.Response.Headers.ContainsKey(ResponseSecurityHeaders.ContentSecurityPolicyHeaderName)
                && inner.Response.Headers.ContainsKey(ResponseSecurityHeaders.ContentTypeOptionsHeaderName)
                && inner.Response.Headers.ContainsKey(ResponseSecurityHeaders.ReferrerPolicyHeaderName);
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(
            sawThemFromInside,
            "the headers were not present when the inner pipeline ran, so anything that short-circuits —"
            + " the rate limiter, static files, the obligations redirect, a 404 — would answer without"
            + " them.");
    }

    /// <summary>
    /// The population this middleware exists for. Placed on an endpoint convention instead, none of
    /// these would carry anything, and the static files are the ones that most want a <c>nosniff</c>.
    /// </summary>
    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status302Found)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task AShortCircuitedResponseStillCarriesThem(int statusCode)
    {
        DefaultHttpContext context = ContextFor("orders.example.com");
        SecurityHeadersMiddleware middleware = new(inner =>
        {
            inner.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey(ResponseSecurityHeaders.ContentSecurityPolicyHeaderName));
        Assert.True(context.Response.Headers.ContainsKey(ResponseSecurityHeaders.ContentTypeOptionsHeaderName));
        Assert.True(context.Response.Headers.ContainsKey(ResponseSecurityHeaders.ReferrerPolicyHeaderName));
    }

    /// <summary>
    /// One policy, not two. The framework's <c>frame-ancestors</c> convention appends with
    /// <c>StringValues.Concat</c>, and a response carrying two <c>Content-Security-Policy</c> values is
    /// enforced as the intersection of both — safe, and impossible to read. <c>Program.cs</c> turns
    /// that convention off; this asserts the middleware would win anyway.
    /// </summary>
    [Fact]
    public async Task ThePolicyReplacesAnythingElseThatSetTheHeader()
    {
        DefaultHttpContext context = ContextFor("orders.example.com");
        context.Response.Headers.Append(
            ResponseSecurityHeaders.ContentSecurityPolicyHeaderName,
            "frame-ancestors 'self'");

        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            1,
            context.Response.Headers[ResponseSecurityHeaders.ContentSecurityPolicyHeaderName].Count);
        Assert.Equal(
            ResponseSecurityHeaders.ContentSecurityPolicyFor("orders.example.com"),
            context.Response.Headers[ResponseSecurityHeaders.ContentSecurityPolicyHeaderName].ToString());
    }

    /// <summary>
    /// The middleware reads the host from the request it is answering, which is why its position in the
    /// pipeline is after <c>PublicOriginMiddleware</c> rather than first.
    /// </summary>
    [Fact]
    public async Task ThePolicyFollowsTheHostTheRequestArrivedOn()
    {
        DefaultHttpContext context = ContextFor("localhost:5099");
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Contains(
            "connect-src 'self' ws://localhost:5099 wss://localhost:5099",
            context.Response.Headers[ResponseSecurityHeaders.ContentSecurityPolicyHeaderName].ToString(),
            StringComparison.Ordinal);
    }

    private static DefaultHttpContext ContextFor(string host)
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString(host);
        return context;
    }
}
