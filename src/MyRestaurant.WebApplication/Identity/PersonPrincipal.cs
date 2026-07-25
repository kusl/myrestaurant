using System.Security.Claims;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Reads the signed-in person's identifier off a <see cref="ClaimsPrincipal"/>
/// (TECHNICAL_SPECIFICATION §3.1). Surfaces that need to scope a query to "me" — the table join flow
/// (§4.4), the sittings index (§11.1), and the guest order surface that follows — all need the
/// <c>person.person_identifier</c> UUIDv7, and they should not each re-derive it or reach for a
/// <c>UserManager</c> round trip just to parse a claim that is already in the cookie.
///
/// <para>The claim read is <see cref="ClaimTypes.NameIdentifier"/>, which is the default
/// <c>ClaimsIdentityOptions.UserIdClaimType</c> that
/// <see cref="Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory{TUser}"/> writes the user id
/// into — and this application never reconfigures it (see
/// <see cref="RestaurantClaimsPrincipalFactory"/>, which adds claims on top of the base factory rather
/// than replacing its identity claims). A principal whose id claim is missing, unparseable, or the
/// all-zero <see cref="Guid"/> yields <c>null</c> rather than a nonsense identifier: callers treat that
/// exactly as "not signed in", which is the safe reading.</para>
/// </summary>
public static class PersonPrincipal
{
    /// <summary>
    /// The person identifier for an authenticated principal, or <c>null</c> when the principal is
    /// anonymous or carries no usable id claim.
    /// </summary>
    public static Guid? IdentifierFor(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
            ? ParseIdentifier(principal.FindFirstValue(ClaimTypes.NameIdentifier))
            : null;

    /// <summary>
    /// Parses a user-id claim value into a person identifier, or <c>null</c> when it is absent,
    /// malformed, or the all-zero <see cref="Guid"/> (which no UUIDv7 key can legitimately be).
    /// </summary>
    public static Guid? ParseIdentifier(string? value)
        => Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty ? identifier : null;
}
