namespace MyRestaurant.WebApplication.Displays;

public sealed record DisplayDeviceCookieValue(Guid DeviceIdentifier, string Secret);

public static class DisplayDeviceCookie
{
    public const string Name = "myrestaurant.display";

    public const string ValuePrefix = "device";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public static string Format(Guid deviceIdentifier, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return $"{ValuePrefix}:{deviceIdentifier:D}:{secret}";
    }

    public static bool TryParse(string? rawValue, out DisplayDeviceCookieValue? value)
    {
        value = null;
        if (string.IsNullOrEmpty(rawValue))
        {
            return false;
        }

        string[] parts = rawValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], ValuePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParse(parts[1], out Guid deviceIdentifier) || deviceIdentifier == Guid.Empty)
        {
            return false;
        }

        if (parts[2].Length == 0)
        {
            return false;
        }

        value = new DisplayDeviceCookieValue(deviceIdentifier, parts[2]);
        return true;
    }

    public static bool WasPresented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.ContainsKey(Name);
    }

    public static bool TryRead(HttpRequest request, out DisplayDeviceCookieValue? value)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryParse(request.Cookies[Name], out value);
    }

    public static void Write(HttpResponse response, Guid deviceIdentifier, string secret)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, Format(deviceIdentifier, secret), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            MaxAge = Lifetime,
        });
    }

    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
        });
    }
}
