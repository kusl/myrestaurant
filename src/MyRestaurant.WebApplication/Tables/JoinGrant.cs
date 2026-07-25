using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MyRestaurant.WebApplication.Tables;

/// <summary>
/// The join grant (TECHNICAL_SPECIFICATION §4.4): proof that <em>this browser</em> presented a valid
/// rotating token for <em>this table</em> at <see cref="IssuedAt"/>. It is the whole payload the
/// specification names — <c>{table_identifier, issued_at}</c> — and nothing more, because nothing more
/// is needed: it is not an authentication, it does not name a person, and it grants no capability
/// beyond "you may now ask to join this one table".
///
/// <para>Its reason to exist is the gap between scanning and joining. A guest who scans an unfamiliar
/// table's QR is very often anonymous, so the flow has to detour through sign-in — or through a
/// registration that includes a WebAuthn ceremony — and the rotating token that got them in will very
/// likely have rotated away by the time they come back (worst case 2 × the rotation window, §4.3).
/// The grant carries the proof across that detour so the guest does not have to re-scan a code they
/// can no longer see. §4.4: "the grant cookie survives the passkey ceremony; that is its purpose."</para>
///
/// <para>It is deliberately short-lived (<c>TABLE_JOIN_GRANT_MINUTES</c>, default 10) and single-use:
/// the join action consumes it and clears the cookie, so it cannot be replayed to join the same table
/// twice or held indefinitely against a table whose secret has since been rotated.</para>
/// </summary>
/// <param name="TableIdentifier">The one table this grant is good for (§4.4).</param>
/// <param name="IssuedAt">When the validating token was accepted — the clock the TTL runs from.</param>
public sealed record JoinGrant(Guid TableIdentifier, DateTimeOffset IssuedAt)
{
    /// <summary>True once the grant is older than <paramref name="lifetime"/> (§4.4: re-scan required).</summary>
    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;

    /// <summary>
    /// True when this grant may be spent on <paramref name="tableIdentifier"/> right now: it names that
    /// table and it has not expired. A grant for a different table is simply not a grant for this one —
    /// scanning table 3 must never let anyone join table 4.
    /// </summary>
    public bool IsUsableFor(Guid tableIdentifier, DateTimeOffset now, TimeSpan lifetime)
        => TableIdentifier == tableIdentifier && !HasExpired(now, lifetime);
}

/// <summary>
/// Protects and reads the <see cref="JoinGrant"/> with ASP.NET Data Protection, exactly as
/// <see cref="Identity.SetupTicketProtector"/> does for the setup wizard: the value in the cookie is
/// encrypted and tamper-evident, so a guest cannot mint a grant for a table they never scanned, and no
/// server-side session store is needed (there is no Redis — ADR-0006).
///
/// <para>The <see cref="Purpose"/> string is distinct from every other protector in the application
/// (the at-rest TOTP secret, the TOTP enrollment ticket, the setup ticket, the auth cookie), so a value
/// produced in one context can never be unprotected as another.</para>
///
/// <para>Registered as a singleton by <c>AddRestaurantTables()</c> — unlike the setup ticket's
/// protector, which one page constructs ad hoc, this one is read by the table surface on every request
/// in the join flow and will be read by the display surface too, so it earns a registration.</para>
/// </summary>
public sealed class JoinGrantProtector
{
    /// <summary>Data-Protection purpose for the join grant. Changing it invalidates in-flight grants.</summary>
    public const string Purpose = "MyRestaurant.Tables.JoinGrant.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public JoinGrantProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <summary>Serializes and protects the grant into the opaque string carried by the cookie.</summary>
    public string Protect(JoinGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return _protector.Protect(JsonSerializer.Serialize(grant, SerializerOptions));
    }

    /// <summary>
    /// Unprotects and deserializes a cookie value. Returns <c>false</c> (never throws) when the value is
    /// missing, tampered, protected with a different purpose or key, or not deserializable — so the join
    /// flow can treat any bad grant as "no grant" and show the friendly re-scan page (§4.4), never an
    /// error that would tell a prober which of those it was.
    /// </summary>
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
            return false; // tampered, wrong key, or not one of ours
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

/// <summary>
/// The join grant's cookie (§4.4). Hardened like the setup and authentication cookies — Secure,
/// HttpOnly, SameSite=Lax — so it is inaccessible to script and is not sent on cross-site requests,
/// while still surviving the top-level navigations the flow depends on: the redirect to sign-in, the
/// return from it, and the same-origin fetches of a WebAuthn ceremony.
///
/// <para>Its <c>MaxAge</c> is the configured <c>TABLE_JOIN_GRANT_MINUTES</c>, but the server never
/// trusts that: the browser's copy expiring is a convenience, and the authoritative check is the
/// <see cref="JoinGrant.IssuedAt"/> inside the protected payload, which a guest cannot edit.</para>
/// </summary>
public static class JoinGrantCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "myrestaurant.join";

    /// <summary>Reads the raw protected value, or <c>null</c> when the browser sent no grant.</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies[Name];
    }

    /// <summary>Writes (or replaces) the protected grant cookie on the response.</summary>
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

    /// <summary>Clears the grant cookie — on consumption (§4.4: single-use) or when it is unusable.</summary>
    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
    }
}
