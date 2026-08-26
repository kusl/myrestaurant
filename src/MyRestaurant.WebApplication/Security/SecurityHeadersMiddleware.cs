using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Security;

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
