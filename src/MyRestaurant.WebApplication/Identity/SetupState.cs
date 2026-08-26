using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

public enum SetupStep
{
    Passkey,
    Totp,
    Review,
}

public sealed record SetupPasskey(
    byte[] CredentialId,
    byte[] PublicKey,
    long SignatureCounter,
    string[]? Transports,
    string Name,
    bool IsUserVerified,
    bool IsBackupEligible,
    bool IsBackedUp);

public sealed record SetupTicket(
    Guid PersonIdentifier,
    DateTimeOffset IssuedAt,
    SetupStep Step,
    string Username,
    string? DisplayName,
    string PasswordHash,
    SetupPasskey? Passkey,
    string? TotpSecretBase32)
{
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;
}

public sealed class SetupTicketProtector
{
    public const string Purpose = "MyRestaurant.Identity.SetupTicket.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public SetupTicketProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(SetupTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return _protector.Protect(JsonSerializer.Serialize(ticket, SerializerOptions));
    }

    public bool TryUnprotect(string? protectedTicket, out SetupTicket? ticket)
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
            ticket = JsonSerializer.Deserialize<SetupTicket>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        return ticket is not null;
    }
}

public static class SetupCookie
{
    public const string Name = "myrestaurant.setup";

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
