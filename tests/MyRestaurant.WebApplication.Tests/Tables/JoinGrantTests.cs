using Microsoft.AspNetCore.DataProtection;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Pure tests for the table join grant (TECHNICAL_SPECIFICATION §4.4): the Data-Protection round-trip
/// carries the table and the issue instant, a tampered or foreign-purpose value is rejected rather than
/// trusted, the embedded <c>issued_at</c> bounds how long a scan stays good, and a grant for one table
/// is never usable on another. No server, no container — these always run. An
/// <see cref="EphemeralDataProtectionProvider"/> gives each test a throwaway key ring, mirroring the
/// setup-ticket tests.
/// </summary>
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

        // Change one character of the payload to a different, still-valid Base64Url character, so the
        // failure is the authentication tag rejecting it rather than a decoding accident.
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
        // Two independent ephemeral providers stand in for "a value that is not one of ours".
        string foreign = NewProtector().Protect(new JoinGrant(TableIdentifier, IssuedAt));

        Assert.False(NewProtector().TryUnprotect(foreign, out JoinGrant? grant));
        Assert.Null(grant);
    }

    [Fact]
    public void TryUnprotect_ValueProtectedForAnotherPurpose_ReturnsFalse()
    {
        // Same key ring, different purpose string: purpose isolation must keep the two apart, which is
        // why the join grant's purpose is distinct from every other protector in the application.
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
        Assert.False(grant.HasExpired(IssuedAt + Lifetime, Lifetime));            // exactly at the edge
        Assert.True(grant.HasExpired(IssuedAt + Lifetime + TimeSpan.FromSeconds(1), Lifetime));
    }

    [Fact]
    public void IsUsableFor_RequiresTheMatchingTableAndALiveGrant()
    {
        JoinGrant grant = new(TableIdentifier, IssuedAt);

        Assert.True(grant.IsUsableFor(TableIdentifier, IssuedAt.AddMinutes(9), Lifetime));

        // Scanning table 1 must never let anyone join table 2 (§4.4).
        Assert.False(grant.IsUsableFor(OtherTable, IssuedAt.AddMinutes(9), Lifetime));

        // Right table, but the scan is stale — the friendly re-scan page, not a join.
        Assert.False(grant.IsUsableFor(TableIdentifier, IssuedAt.AddMinutes(11), Lifetime));
    }

    [Fact]
    public void CookieAndPurpose_AreDistinctFromTheSetupFlow()
    {
        // The join grant and the setup ticket are different short-lived flows on the same origin; a
        // shared cookie name would have one silently clobber the other, and a shared Data-Protection
        // purpose would let a value from one be unprotected as the other.
        Assert.Equal("myrestaurant.join", JoinGrantCookie.Name);
        Assert.NotEqual(MyRestaurant.WebApplication.Identity.SetupCookie.Name, JoinGrantCookie.Name);
        Assert.NotEqual(
            MyRestaurant.WebApplication.Identity.SetupTicketProtector.Purpose,
            JoinGrantProtector.Purpose);
    }

    private static JoinGrantProtector NewProtector() => new(new EphemeralDataProtectionProvider());
}
