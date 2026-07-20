using Microsoft.AspNetCore.DataProtection;
using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Identity;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Identity;

/// <summary>
/// Unit tests for the stateless enrollment ticket (<see cref="TotpEnrollmentTicket"/>) that carries
/// an unconfirmed TOTP secret between the QR-issuing GET and the confirming POST
/// (TECHNICAL_SPECIFICATION §3.4). The ticket is Data-Protection-protected, so tampering, a foreign
/// key, a wrong person, expiry, and malformed payloads must all fail closed. Uses an
/// <see cref="EphemeralDataProtectionProvider"/> — a process-lifetime, in-memory key ring — so no key
/// directory or server is involved.
/// </summary>
public sealed class TotpEnrollmentTicketTests
{
    private static readonly Guid Person = Guid.CreateVersion7();
    private const string SecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private static IDataProtector NewProtector()
        => new EphemeralDataProtectionProvider().CreateProtector("MyRestaurant.Identity.TotpEnrollmentTicket.v1");

    [Fact]
    public void ProtectThenUnprotect_RoundTripsAllFields()
    {
        IDataProtector protector = NewProtector();
        DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        string ticket = new TotpEnrollmentTicket(Person, issuedAt, SecretBase32).Protect(protector);

        Assert.True(TotpEnrollmentTicket.TryUnprotect(protector, ticket, out TotpEnrollmentTicket? decoded));
        Assert.Equal(Person, decoded!.PersonIdentifier);
        Assert.Equal(issuedAt.ToUnixTimeSeconds(), decoded.IssuedAt.ToUnixTimeSeconds());
        Assert.Equal(SecretBase32, decoded.SecretBase32);
    }

    [Fact]
    public void TryUnprotect_TamperedPayload_Fails()
    {
        IDataProtector protector = NewProtector();
        string ticket = new TotpEnrollmentTicket(Person, DateTimeOffset.UtcNow, SecretBase32).Protect(protector);

        string tampered = ticket[..^2] + (ticket[^1] == 'A' ? "BB" : "AA");

        Assert.False(TotpEnrollmentTicket.TryUnprotect(protector, tampered, out _));
    }

    [Fact]
    public void TryUnprotect_DifferentKeyRing_Fails()
    {
        // A ticket protected by one provider cannot be read by another (different ephemeral keys).
        string ticket = new TotpEnrollmentTicket(Person, DateTimeOffset.UtcNow, SecretBase32).Protect(NewProtector());

        Assert.False(TotpEnrollmentTicket.TryUnprotect(NewProtector(), ticket, out _));
    }

    [Fact]
    public void TryUnprotect_EmptyOrNull_Fails()
    {
        IDataProtector protector = NewProtector();

        Assert.False(TotpEnrollmentTicket.TryUnprotect(protector, null, out _));
        Assert.False(TotpEnrollmentTicket.TryUnprotect(protector, string.Empty, out _));
    }

    [Fact]
    public void HasExpired_IsTrueOnlyPastTheLifetime()
    {
        DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        TotpEnrollmentTicket ticket = new(Person, issuedAt, SecretBase32);

        Assert.False(ticket.HasExpired(issuedAt, Lifetime));
        Assert.False(ticket.HasExpired(issuedAt + Lifetime, Lifetime));               // exactly at the edge
        Assert.True(ticket.HasExpired(issuedAt + Lifetime + TimeSpan.FromSeconds(1), Lifetime));
    }

    [Fact]
    public void WrongPerson_IsDetectableAfterUnprotect()
    {
        // The service checks PersonIdentifier after unprotecting; prove the field survives so that
        // check is meaningful (a ticket minted for one person decodes with that person's id).
        IDataProtector protector = NewProtector();
        string ticket = new TotpEnrollmentTicket(Person, DateTimeOffset.UtcNow, SecretBase32).Protect(protector);

        Assert.True(TotpEnrollmentTicket.TryUnprotect(protector, ticket, out TotpEnrollmentTicket? decoded));
        Assert.NotEqual(Guid.CreateVersion7(), decoded!.PersonIdentifier);
        Assert.Equal(Person, decoded.PersonIdentifier);
    }
}

/// <summary>
/// Unit tests for <see cref="TotpQrCode"/>: the server-side SVG must be a self-contained, inline
/// element (no XML prolog, no DOCTYPE, no external references), carry a viewBox and a non-empty path,
/// and escape its accessible label (TECHNICAL_SPECIFICATION §3.4).
/// </summary>
public sealed class TotpQrCodeTests
{
    private static readonly string Uri = TotpProvisioningUri.Build("Test Bistro", "casey", "GEZDGNBVGY3TQOJQ");

    [Fact]
    public void RenderSvg_StartsWithAnInlineSvgElement()
    {
        string svg = TotpQrCode.RenderSvg(Uri);

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xml", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSvg_HasAViewBoxAndANonEmptyPath()
    {
        string svg = TotpQrCode.RenderSvg(Uri);

        Assert.Contains("viewBox=\"0 0 ", svg, StringComparison.Ordinal);
        Assert.Contains("<path d=\"M", svg, StringComparison.Ordinal); // graphics path begins with a moveto
    }

    [Fact]
    public void RenderSvg_MakesNoExternalReferences()
    {
        string svg = TotpQrCode.RenderSvg(Uri);

        // Remove the safe, mandatory namespace declaration
        string cleanedSvg = svg.Replace("xmlns=\"http://www.w3.org/2000/svg\"", "", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("http://", cleanedSvg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", cleanedSvg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderSvg_ProvidesAnAccessibleLabel()
    {
        string svg = TotpQrCode.RenderSvg(Uri);

        Assert.Contains("role=\"img\"", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSvg_RejectsEmptyInput()
        => Assert.ThrowsAny<ArgumentException>(() => TotpQrCode.RenderSvg(string.Empty));
}
