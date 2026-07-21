using Microsoft.AspNetCore.DataProtection;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

/// <summary>
/// Pure tests for the first-administrator setup ticket (TECHNICAL_SPECIFICATION §3.6): the
/// Data-Protection round-trip carries every field a step needs (including the confirmed passkey and
/// the TOTP secret), a tampered or foreign-key value is rejected rather than trusted, and the
/// embedded issued-at bounds how long a setup session stays valid. No server, no container — these
/// always run. An <see cref="EphemeralDataProtectionProvider"/> gives each test a throwaway key ring.
/// </summary>
public sealed class SetupTicketTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Protect_ThenTryUnprotect_RoundTripsEveryField()
    {
        SetupTicketProtector protector = NewProtector();
        SetupPasskey passkey = new(
            CredentialId: [1, 2, 3, 4],
            PublicKey: [9, 8, 7, 6, 5],
            SignatureCounter: 42,
            Transports: ["internal", "hybrid"],
            Name: "Setup passkey",
            IsUserVerified: true,
            IsBackupEligible: true,
            IsBackedUp: false);
        SetupTicket original = new(
            PersonIdentifier: Guid.NewGuid(),
            IssuedAt: IssuedAt,
            Step: SetupStep.Review,
            Username: "chef",
            DisplayName: "Head Chef",
            PasswordHash: "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHQ$dGFndGFndGFn",
            Passkey: passkey,
            TotpSecretBase32: "JBSWY3DPEHPK3PXP");

        string protectedTicket = protector.Protect(original);
        bool ok = protector.TryUnprotect(protectedTicket, out SetupTicket? roundTripped);

        Assert.True(ok);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.PersonIdentifier, roundTripped!.PersonIdentifier);
        Assert.Equal(original.IssuedAt, roundTripped.IssuedAt);
        Assert.Equal(SetupStep.Review, roundTripped.Step);
        Assert.Equal("chef", roundTripped.Username);
        Assert.Equal("Head Chef", roundTripped.DisplayName);
        Assert.Equal(original.PasswordHash, roundTripped.PasswordHash);
        Assert.Equal("JBSWY3DPEHPK3PXP", roundTripped.TotpSecretBase32);

        Assert.NotNull(roundTripped.Passkey);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, roundTripped.Passkey!.CredentialId);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, roundTripped.Passkey.PublicKey);
        Assert.Equal(42, roundTripped.Passkey.SignatureCounter);
        Assert.Equal(new[] { "internal", "hybrid" }, roundTripped.Passkey.Transports);
        Assert.Equal("Setup passkey", roundTripped.Passkey.Name);
        Assert.True(roundTripped.Passkey.IsUserVerified);
        Assert.True(roundTripped.Passkey.IsBackupEligible);
        Assert.False(roundTripped.Passkey.IsBackedUp);
    }

    [Fact]
    public void TryUnprotect_EarlyStepTicket_CarriesNoPasskeyOrSecret()
    {
        SetupTicketProtector protector = NewProtector();
        SetupTicket accountOnly = new(
            Guid.NewGuid(), IssuedAt, SetupStep.Passkey, "chef", DisplayName: null,
            PasswordHash: "hash", Passkey: null, TotpSecretBase32: null);

        Assert.True(protector.TryUnprotect(protector.Protect(accountOnly), out SetupTicket? roundTripped));
        Assert.NotNull(roundTripped);
        Assert.Equal(SetupStep.Passkey, roundTripped!.Step);
        Assert.Null(roundTripped.DisplayName);
        Assert.Null(roundTripped.Passkey);
        Assert.Null(roundTripped.TotpSecretBase32);
    }

    [Theory]
    [InlineData(SetupStep.Passkey)]
    [InlineData(SetupStep.Totp)]
    [InlineData(SetupStep.Review)]
    public void Step_RoundTrips(SetupStep step)
    {
        SetupTicketProtector protector = NewProtector();
        SetupTicket ticket = new(
            Guid.NewGuid(), IssuedAt, step, "chef", null, "hash", null, "JBSWY3DPEHPK3PXP");

        Assert.True(protector.TryUnprotect(protector.Protect(ticket), out SetupTicket? roundTripped));
        Assert.Equal(step, roundTripped!.Step);
    }

    [Fact]
    public void TryUnprotect_TamperedValue_ReturnsFalse()
    {
        SetupTicketProtector protector = NewProtector();
        string protectedTicket = protector.Protect(SampleTicket());

        // Corrupt the version/header byte; Data Protection authenticates its payload, so any change
        // fails the integrity check and Unprotect throws — TryUnprotect must swallow that as "false".
        char[] chars = protectedTicket.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        string tampered = new(chars);

        Assert.False(protector.TryUnprotect(tampered, out SetupTicket? ticket));
        Assert.Null(ticket);
    }

    [Fact]
    public void TryUnprotect_ValueFromAnotherKeyRing_ReturnsFalse()
    {
        // Two ephemeral providers have different keys, so a ticket protected by one cannot be read by
        // the other — the same protection a real deployment gets from its persisted key ring.
        SetupTicketProtector writer = NewProtector();
        SetupTicketProtector reader = NewProtector();

        string protectedByWriter = writer.Protect(SampleTicket());

        Assert.False(reader.TryUnprotect(protectedByWriter, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-protected-ticket")]
    public void TryUnprotect_MissingOrGarbageValue_ReturnsFalse(string? value)
    {
        SetupTicketProtector protector = NewProtector();

        Assert.False(protector.TryUnprotect(value, out SetupTicket? ticket));
        Assert.Null(ticket);
    }

    [Fact]
    public void HasExpired_IsTrueOnlyPastTheLifetime()
    {
        SetupTicket ticket = SampleTicket();
        TimeSpan lifetime = TimeSpan.FromMinutes(30);

        Assert.False(ticket.HasExpired(IssuedAt, lifetime));
        Assert.False(ticket.HasExpired(IssuedAt.AddMinutes(30), lifetime)); // exactly at the edge
        Assert.True(ticket.HasExpired(IssuedAt.AddMinutes(30).AddSeconds(1), lifetime));
    }

    private static SetupTicketProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static SetupTicket SampleTicket() => new(
        Guid.NewGuid(), IssuedAt, SetupStep.Totp, "chef", "Head Chef", "hash", null, "JBSWY3DPEHPK3PXP");
}
