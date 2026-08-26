using Microsoft.AspNetCore.Http;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

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
