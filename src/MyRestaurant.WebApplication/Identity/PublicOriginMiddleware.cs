using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace MyRestaurant.WebApplication.Identity;

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
            request.Host = originHost;
        }
        else if (!_policy.IsTrustedHost(request.Host.Value))
        {
            request.Host = _policy.PublicHost;
        }

        return _next(context);
    }
}
