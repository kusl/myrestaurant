using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.Time;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

public sealed class ObligationsEnforcementTests
{
    [Fact]
    public void NextObligationFor_NoObligationClaims_IsNone()
    {
        ClaimsPrincipal principal = BuildPrincipal();

        Assert.Equal(PostAuthenticationObligation.None, ObligationsEnforcement.NextObligationFor(principal));
    }

    [Fact]
    public void NextObligationFor_MustChangePassword_WinsOverTotp()
    {
        ClaimsPrincipal principal = BuildPrincipal(mustChangePassword: true, mustEnrollTotp: true);

        Assert.Equal(
            PostAuthenticationObligation.ForcePasswordChange,
            ObligationsEnforcement.NextObligationFor(principal));
    }

    [Fact]
    public void NextObligationFor_OnlyTotpFlag_IsForceTotpEnrollment()
    {
        ClaimsPrincipal principal = BuildPrincipal(mustEnrollTotp: true);

        Assert.Equal(
            PostAuthenticationObligation.ForceTotpEnrollment,
            ObligationsEnforcement.NextObligationFor(principal));
    }

    [Fact]
    public void NextObligationFor_ClaimValueOtherThanTrue_DoesNotCount()
    {
        ClaimsIdentity identity = new(authenticationType: "test");
        identity.AddClaim(new Claim(RestaurantClaimTypes.MustChangePassword, "false"));
        ClaimsPrincipal principal = new(identity);

        Assert.Equal(PostAuthenticationObligation.None, ObligationsEnforcement.NextObligationFor(principal));
    }

    [Theory]
    [InlineData(AccountRoutes.ForcedPasswordChange)]
    [InlineData(AccountRoutes.ForcedTotpEnrollment)]
    [InlineData(AccountRoutes.SignOut)]
    [InlineData(AccountRoutes.AccessDenied)]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    [InlineData("/_framework/blazor.web.js")]

    [InlineData(RestaurantClockRoutes.Snapshot)]

    [InlineData(SourceRoutes.Source)]
    public void IsExemptPath_PipelinePagesSignOutHealthAndFrameworkAssets_AreExempt(string path)
    {
        Assert.True(ObligationsEnforcement.IsExemptPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/table")]
    [InlineData("/sign-in")]
    [InlineData("/sign-in/two-factor")]
    [InlineData("/_blazor")]
    [InlineData("/administration")]

    [InlineData("/account/enroll-totp")]
    public void IsExemptPath_EverythingElse_IsBlocked(string path)
    {
        Assert.False(ObligationsEnforcement.IsExemptPath(new PathString(path)));
    }

    [Fact]
    public void RedirectTargetFor_CarriesTheRequestedUrlIncludingQuery()
    {
        DefaultHttpContext context = new();
        context.Request.Path = "/table";
        context.Request.QueryString = new QueryString("?welcome=1");

        string target = ObligationsEnforcement.RedirectTargetFor(
            PostAuthenticationObligation.ForcePasswordChange,
            context.Request);

        Assert.Equal(
            $"{AccountRoutes.ForcedPasswordChange}?ReturnUrl={Uri.EscapeDataString("/table?welcome=1")}",
            target);
    }

    [Fact]
    public void RedirectTargetFor_TotpObligation_TargetsTheEnrollmentPage()
    {
        DefaultHttpContext context = new();
        context.Request.Path = "/";

        string target = ObligationsEnforcement.RedirectTargetFor(
            PostAuthenticationObligation.ForceTotpEnrollment,
            context.Request);

        Assert.StartsWith(AccountRoutes.ForcedTotpEnrollment + "?ReturnUrl=", target, StringComparison.Ordinal);
    }

    [Fact]
    public void PageFor_NoObligation_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ObligationsEnforcement.PageFor(PostAuthenticationObligation.None));
    }

    [Theory]
    [InlineData("/table", "/table")]
    [InlineData("/table?x=1", "/table?x=1")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void SafeLocalReturnUrl_OnlyAcceptsSingleSlashLocalPaths(string? input, string expected)
    {
        Assert.Equal(expected, ObligationsEnforcement.SafeLocalReturnUrl(input));
    }

    private static ClaimsPrincipal BuildPrincipal(bool mustChangePassword = false, bool mustEnrollTotp = false)
    {
        ClaimsIdentity identity = new(authenticationType: "test");

        if (mustChangePassword)
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.MustChangePassword, "true"));
        }

        if (mustEnrollTotp)
        {
            identity.AddClaim(new Claim(RestaurantClaimTypes.MustEnrollTotp, "true"));
        }

        return new ClaimsPrincipal(identity);
    }
}
