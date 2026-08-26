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

public enum TotpEnrollmentConfirmation
{
    Succeeded,
    InvalidCode,
    TicketInvalid,
}

public sealed record TotpEnrollmentStart(
    string SecretBase32,
    string ManualEntrySecret,
    string ProvisioningUri,
    string QrCodeSvg,
    string ProtectedTicket);

public sealed record TotpEnrollmentResult(IReadOnlyList<string> RecoveryCodes);

public sealed class TotpEnrollment
{
    private const string TicketProtectorPurpose = "MyRestaurant.Identity.TotpEnrollmentTicket.v1";

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
            return false;
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

public static class TotpQrCode
{
    private const int QuietZoneModules = 4;
    private const string DarkColor = "#16202b";
    private const string LightColor = "#ffffff";

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
