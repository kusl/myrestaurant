namespace MyRestaurant.WebApplication.Security;

public static class ResponseSecurityHeaders
{
    public const string ContentSecurityPolicyHeaderName = "Content-Security-Policy";

    public const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";

    public const string ReferrerPolicyHeaderName = "Referrer-Policy";

    public const string ContentTypeOptions = "nosniff";

    public const string ReferrerPolicy = "same-origin";

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

    private const string SchemeOnlyWebSocketSources = "ws: wss:";

    public static string ContentSecurityPolicyFor(string? requestHost)
        => string.Join("; ", FixedDirectives) + "; connect-src 'self' " + WebSocketSourcesFor(requestHost);

    public static string WebSocketSourcesFor(string? requestHost)
        => IsExpressibleAsHostSource(requestHost)
            ? "ws://" + requestHost + " wss://" + requestHost
            : SchemeOnlyWebSocketSources;

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

        return labelLength > 0;
    }
}
