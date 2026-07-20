using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using Net.Codecrete.QrCodeGenerator;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The outcome of confirming an enrollment ticket.
/// </summary>
public enum TotpEnrollmentConfirmation
{
    /// <summary>The code verified; the secret and a fresh set of recovery codes were committed.</summary>
    Succeeded,

    /// <summary>The ticket was valid but the code did not verify — the same QR/ticket can be retried.</summary>
    InvalidCode,

    /// <summary>The ticket was missing, tampered with, for another account, or expired — start over.</summary>
    TicketInvalid,
}

/// <summary>
/// Everything an enrollment page needs to render: the Base32 secret (both raw and grouped for manual
/// entry), the provisioning URI, the ready-to-embed SVG QR, and the protected ticket the confirm
/// step round-trips.
/// </summary>
public sealed record TotpEnrollmentStart(
    string SecretBase32,
    string ManualEntrySecret,
    string ProvisioningUri,
    string QrCodeSvg,
    string ProtectedTicket);

/// <summary>
/// The result of a successful confirmation or a recovery-code regeneration: the freshly generated
/// plaintext codes, shown to the user exactly once.
/// </summary>
public sealed record TotpEnrollmentResult(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// TOTP enrollment (TECHNICAL_SPECIFICATION §3.4) for both the voluntary page and the forced
/// re-enrollment obligation (§3.5, obligation 2).
///
/// <para><b>Why a stateless ticket.</b> Enrollment state is derived — a Person is enrolled iff
/// <c>totp_secret_protected IS NOT NULL</c> (there is no pending-secret column). Persisting an
/// unconfirmed secret would therefore flip two-factor on before the user proved they can generate a
/// code. Instead the GET builds a secret and hands the page a Data-Protection-<b>protected</b> ticket
/// (<c>v1|{personId}|{issuedAtUnix}|{base32}</c>); the confirm POST unprotects it, checks it belongs
/// to the signed-in person and is within its 15-minute lifetime, verifies the code, and only then
/// writes the secret. A failed code re-posts the same ticket, so the QR the user already scanned
/// stays valid; an expired ticket yields a fresh QR.</para>
///
/// <para><b>Why it mutates through the store cast.</b> The confirm/regenerate steps operate on the
/// <em>same</em> scoped <see cref="DapperUserStore"/> instance <see cref="UserManager{TUser}"/>
/// resolves (injected here as <see cref="IUserStore{TUser}"/> and cast to the authenticator-key and
/// recovery-code interfaces), so the secret and the forced-enrollment flag are set on the tracked
/// entity and then persisted in a single update via
/// <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/> — the stamp bump is the §3.1 signal that
/// credentials changed. Recovery codes are written separately (their own table/transaction) with the
/// Domain <see cref="RecoveryCode.GenerateSet"/>; the framework's
/// <c>GenerateNewTwoFactorRecoveryCodesAsync</c> is bypassed because it uses a different code format
/// and does not bump the stamp. Regeneration alone does not bump the stamp (it changes no sign-in
/// credential), matching the framework's own behaviour.</para>
/// </summary>
public sealed class TotpEnrollment
{
    /// <summary>Data-Protection purpose for the short-lived enrollment ticket. Distinct from the at-rest secret purpose.</summary>
    private const string TicketProtectorPurpose = "MyRestaurant.Identity.TotpEnrollmentTicket.v1";

    /// <summary>How long a provisioning QR stays confirmable before a fresh one must be generated.</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);

    private readonly UserManager<Person> _userManager;
    private readonly IUserStore<Person> _userStore;
    private readonly ISecurityEventLog _securityEventLog;
    private readonly IDataProtector _ticketProtector;
    private readonly IClock _clock;
    private readonly string _issuer;

    public TotpEnrollment(
        UserManager<Person> userManager,
        IUserStore<Person> userStore,
        ISecurityEventLog securityEventLog,
        IDataProtectionProvider dataProtectionProvider,
        IClock clock,
        string issuer)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(userStore);
        ArgumentNullException.ThrowIfNull(securityEventLog);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(issuer);

        _userManager = userManager;
        _userStore = userStore;
        _securityEventLog = securityEventLog;
        _ticketProtector = dataProtectionProvider.CreateProtector(TicketProtectorPurpose);
        _clock = clock;
        _issuer = issuer;
    }

    /// <summary>
    /// Generates a new secret and everything needed to display it — QR, manual key, provisioning
    /// URI, and the protected ticket to post back. Persists nothing.
    /// </summary>
    public TotpEnrollmentStart BeginEnrollment(Person user)
    {
        ArgumentNullException.ThrowIfNull(user);

        byte[] secret = Rfc6238Totp.GenerateSecret();
        string base32 = Base32Text.Encode(secret);
        string uri = TotpProvisioningUri.Build(_issuer, user.Username, base32);
        string ticket = new TotpEnrollmentTicket(user.PersonIdentifier, _clock.UtcNow, base32)
            .Protect(_ticketProtector);

        return new TotpEnrollmentStart(
            SecretBase32: base32,
            ManualEntrySecret: GroupForManualEntry(base32),
            ProvisioningUri: uri,
            QrCodeSvg: TotpQrCode.RenderSvg(uri),
            ProtectedTicket: ticket);
    }

    /// <summary>
    /// Rebuilds the display for a ticket the user already holds — same secret, same QR, same ticket
    /// — so a mistyped code can be retried without re-scanning. Returns <c>null</c> when the ticket
    /// is unusable (tampered, for another account, or expired), signalling the caller to
    /// <see cref="BeginEnrollment"/> a fresh one instead. Persists nothing.
    /// </summary>
    public TotpEnrollmentStart? ResumeEnrollment(Person user, string protectedTicket)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!TotpEnrollmentTicket.TryUnprotect(_ticketProtector, protectedTicket, out TotpEnrollmentTicket? ticket)
            || ticket!.PersonIdentifier != user.PersonIdentifier
            || ticket.HasExpired(_clock.UtcNow, TicketLifetime))
        {
            return null;
        }

        string uri = TotpProvisioningUri.Build(_issuer, user.Username, ticket.SecretBase32);
        return new TotpEnrollmentStart(
            SecretBase32: ticket.SecretBase32,
            ManualEntrySecret: GroupForManualEntry(ticket.SecretBase32),
            ProvisioningUri: uri,
            QrCodeSvg: TotpQrCode.RenderSvg(uri),
            ProtectedTicket: protectedTicket);
    }

    /// <summary>
    /// Confirms an enrollment: validates the ticket and the code, then (on success) commits the
    /// secret, clears the forced-enrollment obligation, bumps the security stamp, issues a fresh set
    /// of recovery codes, and records one event — <see cref="SecurityEventType.ForcedTotpEnrollmentCompleted"/>
    /// when <paramref name="forced"/>, otherwise <see cref="SecurityEventType.TotpEnrolled"/>.
    /// </summary>
    public async Task<(TotpEnrollmentConfirmation Confirmation, TotpEnrollmentResult? Result)> ConfirmAsync(
        Person user,
        string protectedTicket,
        string code,
        bool forced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!TotpEnrollmentTicket.TryUnprotect(_ticketProtector, protectedTicket, out TotpEnrollmentTicket? ticket)
            || ticket!.PersonIdentifier != user.PersonIdentifier
            || ticket.HasExpired(_clock.UtcNow, TicketLifetime)
            || !Base32Text.TryDecode(ticket.SecretBase32, out byte[] secret))
        {
            return (TotpEnrollmentConfirmation.TicketInvalid, null);
        }

        string normalized = (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);
        if (!Rfc6238Totp.ValidateCode(secret, normalized, _clock.UtcNow))
        {
            return (TotpEnrollmentConfirmation.InvalidCode, null);
        }

        // Commit the secret and clear the obligation on the tracked entity, then persist both in the
        // one update UpdateSecurityStampAsync performs (which also rotates the §3.1 stamp).
        IUserAuthenticatorKeyStore<Person> keyStore = AuthenticatorKeyStore();
        await keyStore.SetAuthenticatorKeyAsync(user, ticket.SecretBase32, cancellationToken).ConfigureAwait(false);
        user.MustEnrollTotp = false;
        await _userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);

        IReadOnlyList<string> recoveryCodes = await IssueRecoveryCodesAsync(user, cancellationToken).ConfigureAwait(false);

        await _securityEventLog.RecordAsync(
            user.PersonIdentifier,
            actorPersonIdentifier: null,
            forced ? SecurityEventType.ForcedTotpEnrollmentCompleted : SecurityEventType.TotpEnrolled,
            cancellationToken).ConfigureAwait(false);

        return (TotpEnrollmentConfirmation.Succeeded, new TotpEnrollmentResult(recoveryCodes));
    }

    /// <summary>
    /// Regenerates the recovery-code set for an already-enrolled account, invalidating the old codes,
    /// and records <see cref="SecurityEventType.RecoveryCodesRegenerated"/>. Does not touch the TOTP
    /// secret or the security stamp.
    /// </summary>
    public async Task<TotpEnrollmentResult> RegenerateRecoveryCodesAsync(
        Person user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        IReadOnlyList<string> recoveryCodes = await IssueRecoveryCodesAsync(user, cancellationToken).ConfigureAwait(false);

        await _securityEventLog.RecordAsync(
            user.PersonIdentifier,
            actorPersonIdentifier: null,
            SecurityEventType.RecoveryCodesRegenerated,
            cancellationToken).ConfigureAwait(false);

        return new TotpEnrollmentResult(recoveryCodes);
    }

    /// <summary>The count of unused recovery codes for <paramref name="user"/>.</summary>
    public Task<int> CountRecoveryCodesAsync(Person user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return RecoveryCodeStore().CountCodesAsync(user, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> IssueRecoveryCodesAsync(Person user, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> codes = RecoveryCode.GenerateSet();
        await RecoveryCodeStore().ReplaceCodesAsync(user, codes, cancellationToken).ConfigureAwait(false);
        return codes;
    }

    private IUserAuthenticatorKeyStore<Person> AuthenticatorKeyStore()
        => _userStore as IUserAuthenticatorKeyStore<Person>
            ?? throw new InvalidOperationException("The configured user store does not support authenticator keys.");

    private IUserTwoFactorRecoveryCodeStore<Person> RecoveryCodeStore()
        => _userStore as IUserTwoFactorRecoveryCodeStore<Person>
            ?? throw new InvalidOperationException("The configured user store does not support recovery codes.");

    /// <summary>Groups a Base32 secret into space-separated blocks of four for easier manual entry.</summary>
    private static string GroupForManualEntry(string base32)
    {
        StringBuilder builder = new(base32.Length + (base32.Length / 4));
        for (int index = 0; index < base32.Length; index++)
        {
            if (index > 0 && index % 4 == 0)
            {
                builder.Append(' ');
            }

            builder.Append(base32[index]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The short-lived, Data-Protection-protected enrollment ticket that carries an unconfirmed secret
/// between the GET that generates the QR and the POST that confirms it, so nothing is persisted until
/// the user proves possession. The payload is <c>v1|{personId:D}|{issuedAtUnixSeconds}|{base32}</c>;
/// protection provides confidentiality and tamper-evidence, and the embedded issued-at plus an
/// explicit lifetime check bound how long a QR stays confirmable.
/// </summary>
public sealed record TotpEnrollmentTicket(Guid PersonIdentifier, DateTimeOffset IssuedAt, string SecretBase32)
{
    private const string Version = "v1";

    public string Protect(IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        string payload = string.Join(
            '|',
            Version,
            PersonIdentifier.ToString("D"),
            IssuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            SecretBase32);

        return protector.Protect(payload);
    }

    public bool HasExpired(DateTimeOffset now, TimeSpan lifetime) => now - IssuedAt > lifetime;

    public static bool TryUnprotect(IDataProtector protector, string? protectedTicket, out TotpEnrollmentTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(protector);

        ticket = null;
        if (string.IsNullOrEmpty(protectedTicket))
        {
            return false;
        }

        string payload;
        try
        {
            payload = protector.Unprotect(protectedTicket);
        }
        catch (CryptographicException)
        {
            return false; // tampered, wrong key, or not one of ours
        }

        string[] parts = payload.Split('|');
        if (parts.Length != 4
            || parts[0] != Version
            || !Guid.TryParseExact(parts[1], "D", out Guid personIdentifier)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long issuedAtUnix)
            || string.IsNullOrEmpty(parts[3]))
        {
            return false;
        }

        ticket = new TotpEnrollmentTicket(
            personIdentifier,
            DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix),
            parts[3]);
        return true;
    }
}

/// <summary>
/// Renders a provisioning URI to a self-contained, inline SVG QR code (TECHNICAL_SPECIFICATION §3.4:
/// server-side, no client calls). The QR modules come from <c>Net.Codecrete.QrCodeGenerator</c>; the
/// element is composed by hand rather than via the library's <c>ToSvgString</c>, which emits an XML
/// prolog and DOCTYPE unsuitable for inlining. A white background rect keeps the code scannable on
/// the panel surface, a four-module quiet zone is baked into both the path and the viewBox, and the
/// <c>aria-label</c> is HTML-escaped so the URI cannot inject markup.
/// </summary>
public static class TotpQrCode
{
    private const int QuietZoneModules = 4;
    private const string DarkColor = "#16202b";  // --ink
    private const string LightColor = "#ffffff"; // --surface-raised

    public static string RenderSvg(string provisioningUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(provisioningUri);

        QrCode qr = QrCode.EncodeText(provisioningUri, QrCode.Ecc.Medium);
        int dimension = qr.Size + (QuietZoneModules * 2);
        string path = qr.ToGraphicsPath(QuietZoneModules);
        string label = HtmlEncoder.Default.Encode("Authenticator setup QR code");

        return
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {dimension} {dimension}\" "
            + $"role=\"img\" aria-label=\"{label}\" class=\"totp-qr-svg\" shape-rendering=\"crispEdges\">"
            + $"<rect width=\"{dimension}\" height=\"{dimension}\" fill=\"{LightColor}\"/>"
            + $"<path d=\"{path}\" fill=\"{DarkColor}\"/>"
            + "</svg>";
    }
}
