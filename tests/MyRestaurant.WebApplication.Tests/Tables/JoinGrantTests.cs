using Microsoft.AspNetCore.DataProtection;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class JoinGrantTests
{
    private static readonly Guid TableIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000ab01");
    private static readonly Guid OtherTable = Guid.Parse("0192f000-0000-7000-8000-00000000ab02");
    private static readonly DateTimeOffset IssuedAt = new(2026, 2, 3, 18, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    [Fact]
    public void Protect_ThenTryUnprotect_RoundTripsTheTableAndInstant()
    {
        JoinGrantProtector protector = NewProtector();
        JoinGrant original = new(TableIdentifier, IssuedAt);

        bool ok = protector.TryUnprotect(protector.Protect(original), out JoinGrant? roundTripped);

        Assert.True(ok);
        Assert.NotNull(roundTripped);
        Assert.Equal(TableIdentifier, roundTripped!.TableIdentifier);
        Assert.Equal(IssuedAt, roundTripped.IssuedAt);
    }

    [Fact]
    public void TryUnprotect_MissingOrEmptyValue_ReturnsFalse()
    {
        JoinGrantProtector protector = NewProtector();

        Assert.False(protector.TryUnprotect(null, out JoinGrant? fromNull));
        Assert.Null(fromNull);

        Assert.False(protector.TryUnprotect(string.Empty, out JoinGrant? fromEmpty));
        Assert.Null(fromEmpty);
    }

    [Fact]
    public void TryUnprotect_TamperedValue_ReturnsFalseRatherThanThrowing()
    {
        JoinGrantProtector protector = NewProtector();
        string protectedGrant = protector.Protect(new JoinGrant(TableIdentifier, IssuedAt));

        char[] characters = protectedGrant.ToCharArray();
        int middle = characters.Length / 2;
        characters[middle] = characters[middle] == 'A' ? 'B' : 'A';
        string tampered = new(characters);

        Assert.NotEqual(protectedGrant, tampered);
        Assert.False(protector.TryUnprotect(tampered, out JoinGrant? grant));
        Assert.Null(grant);
    }

    [Fact]
    public void TryUnprotect_ValueFromADifferentKeyRing_ReturnsFalse()
    {
        string foreign = NewProtector().Protect(new JoinGrant(TableIdentifier, IssuedAt));

        Assert.False(NewProtector().TryUnprotect(foreign, out JoinGrant? grant));
        Assert.Null(grant);
    }

    [Fact]
    public void TryUnprotect_ValueProtectedForAnotherPurpose_ReturnsFalse()
    {
        EphemeralDataProtectionProvider provider = new();
        string otherPurposeValue = provider
            .CreateProtector(MyRestaurant.WebApplication.Identity.SetupTicketProtector.Purpose)
            .Protect("""{"tableIdentifier":"0192f000-0000-7000-8000-00000000ab01","issuedAt":"2026-02-03T18:30:00+00:00"}""");

        Assert.False(new JoinGrantProtector(provider).TryUnprotect(otherPurposeValue, out JoinGrant? grant));
        Assert.Null(grant);
    }

    [Fact]
    public void HasExpired_IsFalseUpToTheLifetimeAndTrueBeyondIt()
    {
        JoinGrant grant = new(TableIdentifier, IssuedAt);

        Assert.False(grant.HasExpired(IssuedAt, Lifetime));
        Assert.False(grant.HasExpired(IssuedAt + Lifetime, Lifetime));
        Assert.True(grant.HasExpired(IssuedAt + Lifetime + TimeSpan.FromSeconds(1), Lifetime));
    }

    [Fact]
    public void IsUsableFor_RequiresTheMatchingTableAndALiveGrant()
    {
        JoinGrant grant = new(TableIdentifier, IssuedAt);

        Assert.True(grant.IsUsableFor(TableIdentifier, IssuedAt.AddMinutes(9), Lifetime));

        Assert.False(grant.IsUsableFor(OtherTable, IssuedAt.AddMinutes(9), Lifetime));

        Assert.False(grant.IsUsableFor(TableIdentifier, IssuedAt.AddMinutes(11), Lifetime));
    }

    [Fact]
    public void CookieAndPurpose_AreDistinctFromTheSetupFlow()
    {
        Assert.Equal("myrestaurant.join", JoinGrantCookie.Name);
        Assert.NotEqual(MyRestaurant.WebApplication.Identity.SetupCookie.Name, JoinGrantCookie.Name);
        Assert.NotEqual(
            MyRestaurant.WebApplication.Identity.SetupTicketProtector.Purpose,
            JoinGrantProtector.Purpose);
    }

    private static JoinGrantProtector NewProtector() => new(new EphemeralDataProtectionProvider());
}
