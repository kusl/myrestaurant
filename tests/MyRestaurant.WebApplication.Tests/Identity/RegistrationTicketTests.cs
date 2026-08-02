using Microsoft.AspNetCore.DataProtection;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

/// <summary>
/// Pure tests for the guest registration ticket (TECHNICAL_SPECIFICATION §4.3, §11.1): the
/// Data-Protection round-trip carries every field the credential step needs, a tampered or foreign
/// value is rejected rather than trusted, the embedded issued-at bounds how long a half-finished
/// registration stays resumable, and <see cref="RegistrationTicket.CanDeclineThePasskey"/> answers the
/// one question the passkey step branches on.
///
/// <para>The last test here is the one worth keeping honest. A registration ticket and a
/// <see cref="SetupTicket"/> carry almost the same fields, and the account the setup one describes is
/// about to be granted <c>administrator</c> — so the two protectors using distinct purposes is not
/// tidiness, it is the thing that stops a value minted on one path being read on the other.</para>
///
/// <para>No server, no container — these always run. An <see cref="EphemeralDataProtectionProvider"/>
/// gives each test a throwaway key ring.</para>
/// </summary>
public sealed class RegistrationTicketTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 3, 4, 18, 30, 0, TimeSpan.Zero);

    private const string SamplePasswordHash =
        "$argon2id$v=19$m=19456,t=2,p=1$Z3Vlc3RndWVzdGd1ZXN0Zw$Z3Vlc3RoYXNoZ3Vlc3RoYXNoZ3Vlc3Q";

    [Fact]
    public void Protect_ThenTryUnprotect_RoundTripsEveryField()
    {
        RegistrationTicketProtector protector = NewProtector();
        RegistrationTicket original = new(
            PersonIdentifier: Guid.NewGuid(),
            IssuedAt: IssuedAt,
            Username: "hungry.guest",
            DisplayName: "Hungry Guest",
            PasswordHash: SamplePasswordHash);

        bool ok = protector.TryUnprotect(protector.Protect(original), out RegistrationTicket? roundTripped);

        Assert.True(ok);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.PersonIdentifier, roundTripped!.PersonIdentifier);
        Assert.Equal(IssuedAt, roundTripped.IssuedAt);
        Assert.Equal("hungry.guest", roundTripped.Username);
        Assert.Equal("Hungry Guest", roundTripped.DisplayName);
        Assert.Equal(SamplePasswordHash, roundTripped.PasswordHash);
    }

    [Fact]
    public void TryUnprotect_PasskeyOnlyTicket_CarriesNoPasswordHash()
    {
        // The passkey-first default (§4.3): the guest left the password blank, so the ticket has no
        // credential in it at all and the passkey step is the only way forward.
        RegistrationTicketProtector protector = NewProtector();
        RegistrationTicket passkeyOnly = new(
            Guid.NewGuid(), IssuedAt, "quiet.guest", DisplayName: null, PasswordHash: null);

        Assert.True(protector.TryUnprotect(protector.Protect(passkeyOnly), out RegistrationTicket? roundTripped));
        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.DisplayName);
        Assert.Null(roundTripped.PasswordHash);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(SamplePasswordHash, true)]
    public void CanDeclineThePasskey_IsTrueOnlyWhenAPasswordWasSet(string? passwordHash, bool expected)
    {
        // §3.3 makes the passkey "always offered, never required" — but declining is only offerable
        // when something else can sign this person in, which is exactly this predicate.
        RegistrationTicket ticket = new(Guid.NewGuid(), IssuedAt, "guest", null, passwordHash);

        Assert.Equal(expected, ticket.CanDeclineThePasskey);
    }

    [Fact]
    public void TryUnprotect_TamperedValue_ReturnsFalse()
    {
        RegistrationTicketProtector protector = NewProtector();
        string protectedTicket = protector.Protect(SampleTicket());

        // Data Protection authenticates its payload, so any change fails the integrity check and
        // Unprotect throws — TryUnprotect must swallow that as "false" so the surface starts over.
        char[] chars = protectedTicket.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';

        Assert.False(protector.TryUnprotect(new string(chars), out RegistrationTicket? ticket));
        Assert.Null(ticket);
    }

    [Fact]
    public void TryUnprotect_ValueFromAnotherKeyRing_ReturnsFalse()
    {
        RegistrationTicketProtector writer = NewProtector();
        RegistrationTicketProtector reader = NewProtector();

        Assert.False(reader.TryUnprotect(writer.Protect(SampleTicket()), out _));
    }

    [Fact]
    public void TryUnprotect_ASetupTicketProtectedUnderItsOwnPurpose_ReturnsFalse()
    {
        // Same key ring, different purpose. Without the purpose split, a value minted by the
        // first-administrator wizard would deserialize cleanly here — the field names overlap — and
        // /register would resume somebody else's half-finished bootstrap.
        EphemeralDataProtectionProvider sharedKeyRing = new();
        SetupTicketProtector setup = new(sharedKeyRing);
        RegistrationTicketProtector registration = new(sharedKeyRing);

        string setupValue = setup.Protect(new SetupTicket(
            Guid.NewGuid(), IssuedAt, SetupStep.Review, "owner", "The Owner",
            SamplePasswordHash, Passkey: null, TotpSecretBase32: "JBSWY3DPEHPK3PXP"));

        Assert.False(registration.TryUnprotect(setupValue, out RegistrationTicket? ticket));
        Assert.Null(ticket);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-protected-ticket")]
    public void TryUnprotect_MissingOrGarbageValue_ReturnsFalse(string? value)
    {
        RegistrationTicketProtector protector = NewProtector();

        Assert.False(protector.TryUnprotect(value, out RegistrationTicket? ticket));
        Assert.Null(ticket);
    }

    [Fact]
    public void HasExpired_IsTrueOnlyPastTheLifetime()
    {
        RegistrationTicket ticket = SampleTicket();
        TimeSpan lifetime = RegistrationCookie.Lifetime;

        Assert.False(ticket.HasExpired(IssuedAt, lifetime));
        Assert.False(ticket.HasExpired(IssuedAt + lifetime, lifetime)); // exactly at the edge
        Assert.True(ticket.HasExpired(IssuedAt + lifetime + TimeSpan.FromSeconds(1), lifetime));
    }

    [Fact]
    public void Cookie_OutlivesTheJoinGrantItTravelsBeside()
    {
        // Not arbitrary: §4.4's grant is the authorization to sit at a table and is deliberately short,
        // while this ticket only has to survive a form and a fingerprint prompt. If it were the shorter
        // of the two, a guest could hold a live grant and still be unable to finish becoming a person —
        // the one combination the join flow has no page for.
        Assert.True(RegistrationCookie.Lifetime > TimeSpan.FromMinutes(10));
    }

    private static RegistrationTicketProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static RegistrationTicket SampleTicket()
        => new(Guid.NewGuid(), IssuedAt, "hungry.guest", "Hungry Guest", SamplePasswordHash);
}
