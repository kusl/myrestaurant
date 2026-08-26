using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MyRestaurant.WebApplication.Tables;

public sealed record JoinGrant(Guid TableIdentifier, DateTimeOffset IssuedAt)
{
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;

    public bool IsUsableFor(Guid tableIdentifier, DateTimeOffset now, TimeSpan lifetime)
        => TableIdentifier == tableIdentifier && !HasExpired(now, lifetime);
}

public sealed class JoinGrantProtector
{
    public const string Purpose = "MyRestaurant.Tables.JoinGrant.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public JoinGrantProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(JoinGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return _protector.Protect(JsonSerializer.Serialize(grant, SerializerOptions));
    }

    public bool TryUnprotect(string? protectedGrant, out JoinGrant? grant)
    {
        grant = null;
        if (string.IsNullOrEmpty(protectedGrant))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(protectedGrant);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            grant = JsonSerializer.Deserialize<JoinGrant>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        return grant is not null;
    }
}

public static class JoinGrantCookie
{
    public const string Name = "myrestaurant.join";

    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies[Name];
    }

    public static void Write(HttpResponse response, string protectedGrant, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, protectedGrant, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            MaxAge = lifetime,
        });
    }

    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
