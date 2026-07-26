using System.Security.Claims;
using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// The claim types carried by a display-device principal, prefixed like
/// <see cref="Identity.RestaurantClaimTypes"/> so they can never collide with framework or standard
/// claims. They are set per request by <see cref="DisplayDeviceAuthenticationMiddleware"/> from the row
/// the credential resolved to — never from anything the browser sent.
/// </summary>
public static class DisplayDeviceClaimTypes
{
    /// <summary>
    /// What kind of principal this is. A device is <b>not</b> a person, so this is how every consumer
    /// tells them apart without inspecting the authentication type.
    /// </summary>
    public const string PrincipalKind = "myrestaurant:principal_kind";

    /// <summary>The table this display may show — §3.7's "table claim", matched against <c>{table}</c>.</summary>
    public const string TableIdentifier = "myrestaurant:display_table";

    /// <summary>That table's label, so the full-screen heading needs no query (§11.5).</summary>
    public const string TableLabel = "myrestaurant:display_table_label";
}

/// <summary>
/// Builds and reads the display-device principal (TECHNICAL_SPECIFICATION §3.7, §4.2). §0 is emphatic
/// that this is "a device principal, kind <c>table_display</c>; never a person", and the shape here
/// enforces that in both directions:
///
/// <list type="bullet">
///   <item><description><b>No role claims.</b> <c>table_display</c> is carried as
///   <see cref="DisplayDeviceClaimTypes.PrincipalKind"/>, not as a role, so it can never satisfy
///   <c>RequireRole</c> and the four §3.7 area policies remain unreachable to a screen. §3.7 says the
///   same thing from the database side: <c>table_display</c> is never a <c>person_role</c>
///   row.</description></item>
///   <item><description><b>No person identifier.</b> <see cref="Identity.PersonPrincipal.IdentifierFor"/>
///   reads <see cref="ClaimTypes.NameIdentifier"/>, which here holds a <c>table_display_device</c> id.
///   Both readers below therefore refuse to answer for anything that is not a display device, and
///   <see cref="Identity.PersonPrincipal"/> would return a device id if it were handed one — which is
///   precisely why no person-scoped surface is ever reached with this principal: the middleware only
///   installs it beneath <see cref="DisplayRoutes.Prefix"/> and on the circuit
///   endpoint.</description></item>
///   <item><description><b>No obligation claims.</b> The §3.5 pipeline reads
///   <c>must_change_password</c> / <c>must_enroll_totp</c>; a device carries neither, so
///   <see cref="Identity.ObligationsEnforcement.NextObligationFor"/> decides "none" and the middleware
///   waves it through. A screen has no credentials to rotate.</description></item>
/// </list>
/// </summary>
public static class DisplayDevicePrincipal
{
    /// <summary>
    /// The identity's authentication type. Non-null is what makes <c>IsAuthenticated</c> true; the value
    /// is distinct from every Identity cookie scheme so a device is never mistaken for a signed-in person.
    /// </summary>
    public const string AuthenticationType = "MyRestaurant.DisplayDevice";

    /// <summary>The <see cref="DisplayDeviceClaimTypes.PrincipalKind"/> value for a table display (§0).</summary>
    public const string PrincipalKind = "table_display";

    /// <summary>
    /// Builds the principal for an authenticated device. Every value comes from the database row the
    /// credential resolved to, so a forged cookie cannot inject a table claim.
    /// </summary>
    public static ClaimsPrincipal Create(DisplayDeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, session.DeviceIdentifier.ToString("D")),
            new Claim(ClaimTypes.Name, session.DeviceLabel),
            new Claim(DisplayDeviceClaimTypes.PrincipalKind, PrincipalKind),
            new Claim(DisplayDeviceClaimTypes.TableIdentifier, session.TableIdentifier.ToString("D")),
            new Claim(DisplayDeviceClaimTypes.TableLabel, session.TableLabel),
        ];

        // The role type is named explicitly even though no role claim is ever added: it fixes what
        // IsInRole would look for, so the "a device holds no role" guarantee does not depend on a default.
        ClaimsIdentity identity = new(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>True when <paramref name="principal"/> is an authenticated display device.</summary>
    public static bool IsDisplayDevice(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
        && string.Equals(
            principal.FindFirstValue(DisplayDeviceClaimTypes.PrincipalKind),
            PrincipalKind,
            StringComparison.Ordinal);

    /// <summary>The device identifier, or <c>null</c> when the principal is not a display device.</summary>
    public static Guid? DeviceIdentifierFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? ParseIdentifier(principal!.FindFirstValue(ClaimTypes.NameIdentifier))
            : null;

    /// <summary>
    /// The one table this device may render — §3.7's "device principal whose table claim matches
    /// <c>{table}</c>". <c>null</c> when the principal is not a display device.
    /// </summary>
    public static Guid? TableIdentifierFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? ParseIdentifier(principal!.FindFirstValue(DisplayDeviceClaimTypes.TableIdentifier))
            : null;

    /// <summary>That table's label, or <c>null</c> when the principal is not a display device.</summary>
    public static string? TableLabelFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? principal!.FindFirstValue(DisplayDeviceClaimTypes.TableLabel)
            : null;

    /// <summary>The device's own label, or <c>null</c> when the principal is not a display device.</summary>
    public static string? DeviceLabelFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? principal!.FindFirstValue(ClaimTypes.Name)
            : null;

    /// <summary>
    /// Parses an identifier claim, or <c>null</c> when it is absent, malformed, or the all-zero
    /// <see cref="Guid"/> (which no UUIDv7 key can legitimately be) — the same reading
    /// <see cref="Identity.PersonPrincipal.ParseIdentifier"/> applies.
    /// </summary>
    private static Guid? ParseIdentifier(string? value)
        => Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty ? identifier : null;
}
