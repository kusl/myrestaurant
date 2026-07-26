using System.Security.Claims;
using MyRestaurant.DataAccess.Displays;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

/// <summary>
/// Unit tests for <see cref="DisplayDevicePrincipal"/> (TECHNICAL_SPECIFICATION §0, §3.7, §4.2). The
/// specification is blunt that a display is "a device principal, kind <c>table_display</c>; never a
/// person", and this file is where that sentence is enforced in both directions: a device carries the
/// claims the display surface needs, and it carries none of the claims that would let it act as a person
/// — no role, and nothing the §3.5 obligations pipeline reads. Pure: no server, no container.
/// </summary>
public sealed class DisplayDevicePrincipalTests
{
    private static readonly Guid DeviceIdentifier = Guid.Parse("0192f100-0000-7000-8000-0000000000d1");
    private static readonly Guid TableIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000ab01");

    private static readonly DisplayDeviceSession Session = new(
        DeviceIdentifier,
        TableIdentifier,
        DeviceLabel: "Window tablet",
        TableLabel: "Table 4",
        TableIsActive: true);

    [Fact]
    public void Create_ProducesAnAuthenticatedDeviceTheReadersUnderstand()
    {
        ClaimsPrincipal principal = DisplayDevicePrincipal.Create(Session);

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(DisplayDevicePrincipal.AuthenticationType, principal.Identity!.AuthenticationType);
        Assert.True(DisplayDevicePrincipal.IsDisplayDevice(principal));

        Assert.Equal(DeviceIdentifier, DisplayDevicePrincipal.DeviceIdentifierFor(principal));
        Assert.Equal(TableIdentifier, DisplayDevicePrincipal.TableIdentifierFor(principal));
        Assert.Equal("Table 4", DisplayDevicePrincipal.TableLabelFor(principal));
        Assert.Equal("Window tablet", DisplayDevicePrincipal.DeviceLabelFor(principal));
        Assert.Equal("Window tablet", principal.Identity.Name);
    }

    [Fact]
    public void Create_GrantsNoRoleAtAll()
    {
        ClaimsPrincipal principal = DisplayDevicePrincipal.Create(Session);

        // §3.7: table_display is never a person_role, and the four area policies are RequireRole-based,
        // so a device must fail every one of them by construction rather than by configuration.
        Assert.Empty(principal.FindAll(ClaimTypes.Role));
        Assert.False(principal.IsInRole("administrator"));
        Assert.False(principal.IsInRole("kitchen"));
        Assert.False(principal.IsInRole("counter"));
        Assert.False(principal.IsInRole(DisplayDevicePrincipal.PrincipalKind));
    }

    [Fact]
    public void Create_CarriesNoObligationClaims()
    {
        ClaimsPrincipal principal = DisplayDevicePrincipal.Create(Session);

        // A screen has no password to rotate and no authenticator to enrol, so the §3.5 pipeline must
        // decide "nothing outstanding" and wave it through rather than trapping it on an account page.
        Assert.Null(principal.FindFirst(RestaurantClaimTypes.MustChangePassword));
        Assert.Null(principal.FindFirst(RestaurantClaimTypes.MustEnrollTotp));
        Assert.Equal(
            PostAuthenticationObligation.None,
            ObligationsEnforcement.NextObligationFor(principal));
    }

    [Fact]
    public void Readers_RefuseAnAnonymousPrincipal()
    {
        ClaimsPrincipal anonymous = new(new ClaimsIdentity());

        Assert.False(DisplayDevicePrincipal.IsDisplayDevice(anonymous));
        Assert.Null(DisplayDevicePrincipal.DeviceIdentifierFor(anonymous));
        Assert.Null(DisplayDevicePrincipal.TableIdentifierFor(anonymous));
        Assert.Null(DisplayDevicePrincipal.TableLabelFor(anonymous));
        Assert.Null(DisplayDevicePrincipal.DeviceLabelFor(anonymous));
        Assert.False(DisplayDevicePrincipal.IsDisplayDevice(null));
        Assert.Null(DisplayDevicePrincipal.DeviceIdentifierFor(null));
    }

    [Fact]
    public void Readers_RefuseASignedInPersonEvenThoughItHasANameIdentifier()
    {
        // The decisive case: a person's principal also carries ClaimTypes.NameIdentifier, so the readers
        // must gate on the principal-kind claim, not on the presence of an id.
        ClaimsPrincipal person = new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.Parse("0192f200-0000-7000-8000-00000000cc01").ToString("D")),
                new Claim(ClaimTypes.Name, "ada"),
                new Claim(ClaimTypes.Role, "administrator"),
            ],
            "Identity.Application",
            ClaimTypes.Name,
            ClaimTypes.Role));

        Assert.False(DisplayDevicePrincipal.IsDisplayDevice(person));
        Assert.Null(DisplayDevicePrincipal.DeviceIdentifierFor(person));
        Assert.Null(DisplayDevicePrincipal.TableIdentifierFor(person));
        Assert.Null(DisplayDevicePrincipal.TableLabelFor(person));
    }

    [Fact]
    public void Readers_RefuseAKindClaimOnAnUnauthenticatedIdentity()
    {
        // An identity with no authentication type is not authenticated, whatever claims it carries — so
        // a forged kind claim on a bare identity cannot manufacture a device.
        ClaimsPrincipal forged = new(new ClaimsIdentity(
        [
            new Claim(DisplayDeviceClaimTypes.PrincipalKind, DisplayDevicePrincipal.PrincipalKind),
            new Claim(ClaimTypes.NameIdentifier, DeviceIdentifier.ToString("D")),
            new Claim(DisplayDeviceClaimTypes.TableIdentifier, TableIdentifier.ToString("D")),
        ]));

        Assert.False(DisplayDevicePrincipal.IsDisplayDevice(forged));
        Assert.Null(DisplayDevicePrincipal.TableIdentifierFor(forged));
    }

    [Fact]
    public void TableIdentifierFor_RefusesTheAllZeroGuid()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [
                new Claim(DisplayDeviceClaimTypes.PrincipalKind, DisplayDevicePrincipal.PrincipalKind),
                new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString("D")),
                new Claim(DisplayDeviceClaimTypes.TableIdentifier, Guid.Empty.ToString("D")),
            ],
            DisplayDevicePrincipal.AuthenticationType));

        // Guid.Empty would otherwise compare equal to a route value of the same, which is exactly the
        // kind of accidental match the table-claim rule exists to prevent (§3.7).
        Assert.Null(DisplayDevicePrincipal.DeviceIdentifierFor(principal));
        Assert.Null(DisplayDevicePrincipal.TableIdentifierFor(principal));
    }
}
