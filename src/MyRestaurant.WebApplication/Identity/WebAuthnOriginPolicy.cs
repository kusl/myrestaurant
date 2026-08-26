using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

public sealed class WebAuthnOriginPolicy
{
    private readonly string _publicOriginHostPort;
    private readonly bool _publicOriginIsLoopback;
    private readonly IReadOnlyList<(string Scheme, string Host)> _patterns;

    public WebAuthnOriginPolicy(string publicOrigin, IEnumerable<string> trustedOriginPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);
        ArgumentNullException.ThrowIfNull(trustedOriginPatterns);

        Uri origin = new(publicOrigin, UriKind.Absolute);
        PublicHost = origin.IsDefaultPort ? new HostString(origin.Host) : new HostString(origin.Host, origin.Port);
        _publicOriginHostPort = PublicHost.Value?.ToLowerInvariant() ?? string.Empty;
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

    public HostString PublicHost { get; }

    public bool IsTrustedOrigin(string? origin)
    {
        if (!TrySplitOrigin(origin, out string scheme, out string host))
        {
            return false;
        }

        if (IsLoopbackHost(HostOnly(host)))
        {
            return scheme is "https" or "http";
        }

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

    private static bool HostMatches(string patternHost, string candidateHost)
    {
        if (!patternHost.StartsWith("*.", StringComparison.Ordinal))
        {
            return patternHost == candidateHost;
        }

        string suffix = patternHost[1..];
        if (!candidateHost.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string label = candidateHost[..^suffix.Length];
        return label.Length > 0 && !label.AsSpan().ContainsAny('.', ':', '/');
    }

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

        host = StripDefaultPort(scheme, host);
        return true;
    }

    private static string StripDefaultPort(string scheme, string hostPort)
    {
        int close = hostPort.LastIndexOf(']');
        int colon = hostPort.LastIndexOf(':');
        if (colon <= close)
        {
            return hostPort;
        }

        string port = hostPort[(colon + 1)..];
        bool isDefault = (scheme == "https" && port == "443") || (scheme == "http" && port == "80");
        return isDefault ? hostPort[..colon] : hostPort;
    }

    private static string HostOnly(string hostPort)
    {
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
