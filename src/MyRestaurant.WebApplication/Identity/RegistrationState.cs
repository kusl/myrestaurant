using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

public sealed record RegistrationTicket(
    Guid PersonIdentifier,
    DateTimeOffset IssuedAt,
    string Username,
    string? DisplayName,
    string? PasswordHash)
{
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;

    public bool CanDeclineThePasskey => !string.IsNullOrEmpty(PasswordHash);
}

public sealed class RegistrationTicketProtector
{
    public const string Purpose = "MyRestaurant.Identity.RegistrationTicket.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public RegistrationTicketProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(RegistrationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return _protector.Protect(JsonSerializer.Serialize(ticket, SerializerOptions));
    }

    public bool TryUnprotect(string? protectedTicket, out RegistrationTicket? ticket)
    {
        ticket = null;
        if (string.IsNullOrEmpty(protectedTicket))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(protectedTicket);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            ticket = JsonSerializer.Deserialize<RegistrationTicket>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        return ticket is not null;
    }
}

public static class RegistrationCookie
{
    public const string Name = "myrestaurant.registration";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static void Write(HttpResponse response, string protectedTicket)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, protectedTicket, new CookieOptions
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
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
