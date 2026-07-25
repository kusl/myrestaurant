using System.Security.Claims;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

/// <summary>
/// Pure tests for <see cref="PersonPrincipal"/> (TECHNICAL_SPECIFICATION §3.1) — the one place the
/// surfaces read "who is this?" off the authentication cookie's principal. The interesting cases are all
/// the ways it must answer <c>null</c> rather than something plausible-looking: an anonymous visitor, an
/// authenticated identity with no id claim, a malformed value, and the all-zero <see cref="Guid"/>, which
/// no UUIDv7 primary key can legitimately be. Every caller treats <c>null</c> as "not signed in", so a
/// wrong answer here would either hide a member's table or scope a query to nobody.
/// </summary>
public sealed class PersonPrincipalTests
{
    private const string AuthenticationType = "TestCookie";

    private static readonly Guid PersonIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000000aa");

    [Fact]
    public void IdentifierFor_AuthenticatedPrincipalWithNameIdentifier_ReturnsTheParsedIdentifier()
    {
        ClaimsPrincipal principal = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, PersonIdentifier.ToString("D")));

        Assert.Equal(PersonIdentifier, PersonPrincipal.IdentifierFor(principal));
    }

    [Fact]
    public void IdentifierFor_IsCaseAndFormatInsensitiveAboutTheClaimValue()
    {
        // Identity writes the value with UserManager.GetUserIdAsync (Guid.ToString()); accept the
        // braced/upper spellings too rather than silently dropping a real member.
        Assert.Equal(
            PersonIdentifier,
            PersonPrincipal.IdentifierFor(Authenticated(
                new Claim(ClaimTypes.NameIdentifier, PersonIdentifier.ToString("D").ToUpperInvariant()))));

        Assert.Equal(
            PersonIdentifier,
            PersonPrincipal.IdentifierFor(Authenticated(
                new Claim(ClaimTypes.NameIdentifier, PersonIdentifier.ToString("B")))));
    }

    [Fact]
    public void IdentifierFor_AnonymousPrincipal_ReturnsNull()
    {
        // No authentication type → IsAuthenticated is false, even with an id claim attached.
        ClaimsPrincipal anonymous = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, PersonIdentifier.ToString("D"))]));

        Assert.Null(PersonPrincipal.IdentifierFor(anonymous));
        Assert.Null(PersonPrincipal.IdentifierFor(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(PersonPrincipal.IdentifierFor(null));
    }

    [Fact]
    public void IdentifierFor_AuthenticatedWithoutAnIdentifierClaim_ReturnsNull()
    {
        ClaimsPrincipal principal = Authenticated(new Claim(ClaimTypes.Name, "ada"));

        Assert.Null(PersonPrincipal.IdentifierFor(principal));
    }

    [Fact]
    public void IdentifierFor_MalformedOrEmptyIdentifier_ReturnsNull()
    {
        Assert.Null(PersonPrincipal.IdentifierFor(Authenticated(new Claim(ClaimTypes.NameIdentifier, "ada"))));
        Assert.Null(PersonPrincipal.IdentifierFor(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString("D")))));
    }

    [Fact]
    public void ParseIdentifier_RejectsNullEmptyGarbageAndTheZeroGuid()
    {
        Assert.Null(PersonPrincipal.ParseIdentifier(null));
        Assert.Null(PersonPrincipal.ParseIdentifier(string.Empty));
        Assert.Null(PersonPrincipal.ParseIdentifier("   "));
        Assert.Null(PersonPrincipal.ParseIdentifier("not-a-guid"));
        Assert.Null(PersonPrincipal.ParseIdentifier(Guid.Empty.ToString("D")));

        Assert.Equal(PersonIdentifier, PersonPrincipal.ParseIdentifier(PersonIdentifier.ToString("D")));
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, AuthenticationType));
}
