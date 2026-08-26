using System.Security.Claims;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

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
