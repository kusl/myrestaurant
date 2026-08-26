using System.Security.Claims;
using MyRestaurant.DataAccess.Displays;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

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

        Assert.Null(DisplayDevicePrincipal.DeviceIdentifierFor(principal));
        Assert.Null(DisplayDevicePrincipal.TableIdentifierFor(principal));
    }
}
