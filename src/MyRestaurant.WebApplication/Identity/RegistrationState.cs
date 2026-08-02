using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The in-progress state of the guest registration surface (TECHNICAL_SPECIFICATION §4.3, §11.1).
/// Nothing here is written to the database until the credential step commits the whole account in one
/// transaction (<see cref="MyRestaurant.DataAccess.Identity.IGuestRegistration"/>); until then it
/// lives only in a Data-Protection-protected cookie, so it is confidential, tamper-evident, and
/// self-contained (no server-side session store — there is no Redis, ADR-0006).
///
/// <para><b>Why the ticket exists at all, when this is only a two-step form.</b> A WebAuthn
/// attestation needs a user handle and a username <em>before</em> the account exists — the browser is
/// told who it is creating a credential for, and the authenticator stores that. So the person's
/// UUIDv7 must be minted at the details step and be the same value the <c>person</c> row later
/// carries, or a discoverable-credential sign-in would return a handle matching nobody. This is the
/// same problem <c>/setup</c> solves with <see cref="SetupTicket"/> and the same solution (§3.6).</para>
///
/// <para><b>Why there is no step enum.</b> <see cref="SetupTicket"/> carries one because the wizard
/// has three ordered steps after the account details and each must be unskippable. Registration has
/// exactly one: the ticket's existence <em>is</em> the state. A single-valued enum would be ceremony
/// pretending to be a state machine.</para>
///
/// <para><see cref="PasswordHash"/> is the Argon2id PHC string (§3.2) — the plaintext is hashed at the
/// details step and never carried — or <c>null</c> when the guest is heading for a passkey-only
/// account, in which case the passkey step has no "not now" and cannot be skipped.</para>
/// </summary>
public sealed record RegistrationTicket(
    Guid PersonIdentifier,
    DateTimeOffset IssuedAt,
    string Username,
    string? DisplayName,
    string? PasswordHash)
{
    /// <summary>True once the ticket is older than <paramref name="lifetime"/> (start over).</summary>
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;

    /// <summary>
    /// True when the guest set a password at the details step, and therefore already has a credential:
    /// the passkey step may offer "not now" (§3.3 — "always offered, never required, never a gate for
    /// guests"). False for a passkey-only registration, where declining would leave an account nobody
    /// could sign into and the data layer would refuse the write.
    /// </summary>
    public bool CanDeclineThePasskey => !string.IsNullOrEmpty(PasswordHash);
}

/// <summary>
/// Protects and reads the <see cref="RegistrationTicket"/> with ASP.NET Data Protection. The purpose
/// is distinct from every other protector in the application (the setup ticket, the at-rest TOTP
/// secret, the TOTP enrollment ticket, the auth and join-grant cookies), so a value minted in one
/// context can never be unprotected as another — which matters here more than most, because a setup
/// ticket and a registration ticket carry nearly the same fields and the setup one belongs to an
/// account that is about to be granted <c>administrator</c>. Constructed ad hoc from the ambient
/// <see cref="IDataProtectionProvider"/>; it holds no state worth registering in DI.
/// </summary>
public sealed class RegistrationTicketProtector
{
    /// <summary>Data-Protection purpose for the registration cookie. Changing it invalidates in-flight registrations.</summary>
    public const string Purpose = "MyRestaurant.Identity.RegistrationTicket.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public RegistrationTicketProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <summary>Serializes and protects the ticket into the opaque string carried by the cookie.</summary>
    public string Protect(RegistrationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return _protector.Protect(JsonSerializer.Serialize(ticket, SerializerOptions));
    }

    /// <summary>
    /// Unprotects and deserializes a cookie value. Returns <c>false</c> (never throws) when the value
    /// is missing, tampered, protected with a different purpose or key ring, or not deserializable —
    /// so the surface can treat any bad ticket as "no ticket" and start the guest over.
    /// </summary>
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
            return false; // tampered, wrong key, or protected under another purpose
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

/// <summary>
/// The registration surface's cookie (§4.3). Short-lived and Data-Protection-protected (see
/// <see cref="RegistrationTicketProtector"/>); hardened like the auth and setup cookies (Secure,
/// HttpOnly, SameSite=Lax) so it survives the same-origin ceremony fetch while staying inaccessible to
/// script and off-origin requests. Written and cleared by the static-SSR <c>Register</c> page, and read
/// by the anonymous registration passkey-options endpoint to recover the pending user handle.
///
/// <para><b>It is deliberately not the join grant, and does not replace it.</b> The join grant
/// (<see cref="Tables.JoinGrantCookie"/>, §4.4) is what survives this detour and lets the guest join
/// the table they scanned without a fresh token; this one only carries who they are becoming. They
/// have different lifetimes on purpose: the grant is bounded by <c>TABLE_JOIN_GRANT_MINUTES</c>
/// because it is an authorization to sit at a table, while this is bounded by how long a person might
/// reasonably take over a form and a fingerprint prompt.</para>
/// </summary>
public static class RegistrationCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "myrestaurant.registration";

    /// <summary>
    /// How long a half-finished registration stays resumable. Thirty minutes, matching
    /// <see cref="SetupCookie.Lifetime"/>: the two surfaces present the same kind of pause (a form,
    /// then a platform credential prompt that a person may fumble), and the value guards nothing but a
    /// username and an Argon2id hash that no account yet refers to.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Writes (or overwrites) the protected registration ticket cookie on the response.</summary>
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

    /// <summary>Clears the ticket cookie (on completion, on "start over", or when it is stale).</summary>
    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
