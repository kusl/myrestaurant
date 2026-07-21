using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Where the first-administrator wizard is (TECHNICAL_SPECIFICATION §3.6). The account step has no
/// value here — it is the initial state, entered when there is no ticket at all. Each subsequent step
/// is reached only by completing the previous one, so the wizard cannot be skipped ahead: the page
/// renders whatever the (tamper-proof) ticket says, and the passkey and TOTP steps each require their
/// ceremony to have succeeded before the ticket advances.
/// </summary>
public enum SetupStep
{
    /// <summary>Passkey registration (WebAuthn attestation) — reached after the account details are captured.</summary>
    Passkey,

    /// <summary>Authenticator (TOTP) enrollment — reached after a passkey is confirmed.</summary>
    Totp,

    /// <summary>Final review and commit — reached after the TOTP code is confirmed.</summary>
    Review,
}

/// <summary>
/// A confirmed passkey carried between the attestation step and the final commit. Mirrors the fields
/// of <c>UserPasskeyInfo</c> the store persists — the attestation object and client-data JSON are not
/// carried, exactly as they are not stored (attestation is 'none', §3.3). Byte arrays serialize as
/// Base64 in the ticket JSON; the value is then Data-Protection-encrypted, so it is confidential and
/// tamper-evident in the cookie.
/// </summary>
public sealed record SetupPasskey(
    byte[] CredentialId,
    byte[] PublicKey,
    long SignatureCounter,
    string[]? Transports,
    string Name,
    bool IsUserVerified,
    bool IsBackupEligible,
    bool IsBackedUp);

/// <summary>
/// The accumulating state of the <c>/setup</c> wizard (§3.6). Nothing here is written to the database
/// until the final step commits it in one transaction; until then it lives only in a
/// Data-Protection-protected cookie, so it is confidential, tamper-evident, and self-contained (no
/// server-side session store — there is no Redis, ADR-0006).
///
/// <para>The <see cref="PersonIdentifier"/> is minted once, at the account step, and reused as the
/// WebAuthn user handle for the passkey ceremony so it equals the eventual <c>person</c> row's id —
/// which a discoverable-credential sign-in relies on. <see cref="PasswordHash"/> is the Argon2id PHC
/// string (the plaintext is hashed immediately and never carried). <see cref="Passkey"/> is present
/// from the TOTP step onward; <see cref="TotpSecretBase32"/> is generated when the TOTP step is
/// entered so its QR is stable across re-renders (a mistyped code re-shows the same QR).</para>
/// </summary>
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
    /// <summary>True once the ticket is older than <paramref name="lifetime"/> (the QR/session expired; start over).</summary>
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;
}

/// <summary>
/// Protects and reads the <see cref="SetupTicket"/> with ASP.NET Data Protection. The purpose is
/// distinct from every other protector in the app (the at-rest TOTP secret, the TOTP enrollment
/// ticket, the auth/join cookies), so a value from one context can never be unprotected as another.
/// Constructed ad hoc from the ambient <see cref="IDataProtectionProvider"/> — it holds no state
/// worth registering in DI.
/// </summary>
public sealed class SetupTicketProtector
{
    /// <summary>Data-Protection purpose for the setup wizard cookie. Do not change without invalidating in-flight setups.</summary>
    public const string Purpose = "MyRestaurant.Identity.SetupTicket.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public SetupTicketProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <summary>Serializes and protects the ticket into the opaque string carried by the setup cookie.</summary>
    public string Protect(SetupTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return _protector.Protect(JsonSerializer.Serialize(ticket, SerializerOptions));
    }

    /// <summary>
    /// Unprotects and deserializes a cookie value. Returns <c>false</c> (never throws) when the value
    /// is missing, tampered, protected with a different purpose/key, or not deserializable — so the
    /// wizard can treat any bad ticket as "no ticket" and start over.
    /// </summary>
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
            return false; // tampered, wrong key, or not one of ours
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

/// <summary>
/// The setup wizard's cookie (§3.6). Short-lived and Data-Protection-protected (see
/// <see cref="SetupTicketProtector"/>); hardened like the auth cookie (Secure, HttpOnly,
/// SameSite=Lax) so it survives the same-origin ceremony fetches while staying inaccessible to
/// script and off-origin requests. Written and cleared by the static-SSR <c>Setup</c> page, and read
/// by the anonymous setup passkey-options endpoint to recover the pending user handle.
/// </summary>
public static class SetupCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "myrestaurant.setup";

    /// <summary>How long a setup session (and its passkey/TOTP QR) stays valid before it must be restarted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Writes (or overwrites) the protected setup ticket cookie on the response.</summary>
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

    /// <summary>Clears the setup ticket cookie (on completion, on restart, or when it is stale).</summary>
    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
