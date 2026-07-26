using System.Text;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The three self-editable fields of a person's profile (TECHNICAL_SPECIFICATION §4.6, §11.6), in
/// normalized form: display name, e-mail address, phone number. Everything else on the profile page
/// is a credential managed by its own surface (passkeys, password, authenticator).
///
/// <para><b>Why this is a separate, pure type.</b> The profile page is a static-SSR Razor component,
/// which nothing in this repository can unit-test (no bUnit, §16.1). Normalization and validation are
/// the only parts with real decisions in them, so they live here — no <c>Person</c>, no
/// <c>HttpContext</c>, no database — exactly as <c>ObligationsEnforcement</c>,
/// <c>WebAuthnOriginPolicy</c>, and <c>PairingCode</c> already do for their surfaces.</para>
///
/// <para><b>Validation is deliberately loose.</b> The e-mail address and phone number exist for
/// <em>manual</em> staff escalation only (§4.6): nothing in the system ever sends to them, and no
/// paid sending service is permitted, so there is no deliverability check to be had and nothing is
/// lost by accepting an unusual-but-plausible value. The checks below reject only what is certainly
/// a mistake (no <c>@</c>, letters in a phone number, a stray control character) and let everything
/// else through. A person locked out of their own profile page by an over-strict e-mail regex is a
/// worse outcome than a typo nobody dials.</para>
/// </summary>
/// <param name="DisplayName">The optional human name shown on rosters and kitchen tickets (§3.1, §11.2), or <c>null</c>.</param>
/// <param name="EmailAddress">The optional <c>citext</c> e-mail address — manual escalation only, or <c>null</c>.</param>
/// <param name="PhoneNumber">The optional phone number — manual escalation only, or <c>null</c>.</param>
public sealed record ProfileDetails(string? DisplayName, string? EmailAddress, string? PhoneNumber)
{
    /// <summary>Matches the length the staff-creation form already enforces on a display name.</summary>
    public const int DisplayNameMaximumLength = 120;

    /// <summary>The RFC 5321 maximum for a forward path, which is as good a cap as any.</summary>
    public const int EmailAddressMaximumLength = 254;

    /// <summary>Comfortably over E.164 plus separators and an extension.</summary>
    public const int PhoneNumberMaximumLength = 32;

    /// <summary>
    /// Trims, collapses every internal whitespace run to a single space, drops control characters,
    /// and turns an all-whitespace value into <c>null</c> — the schema's "unset" for all three
    /// columns. Purely reshaping: the result still has to pass <see cref="Validate"/>.
    /// </summary>
    public static ProfileDetails Normalize(string? displayName, string? emailAddress, string? phoneNumber)
        => new(NormalizeText(displayName), NormalizeText(emailAddress), NormalizeText(phoneNumber));

    /// <summary>
    /// Every problem with this (already normalized) set of details, in field order; empty when it is
    /// safe to persist. Returning all of them at once means one round trip fixes one form.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (DisplayName is { } displayName && displayName.Length > DisplayNameMaximumLength)
        {
            problems.Add($"The display name must be {DisplayNameMaximumLength} characters or fewer.");
        }

        if (EmailAddress is { } emailAddress)
        {
            if (emailAddress.Length > EmailAddressMaximumLength)
            {
                problems.Add($"The email address must be {EmailAddressMaximumLength} characters or fewer.");
            }
            else if (!LooksLikeEmailAddress(emailAddress))
            {
                problems.Add("That does not look like an email address. Leave it blank if you would rather not give one.");
            }
        }

        if (PhoneNumber is { } phoneNumber)
        {
            if (phoneNumber.Length > PhoneNumberMaximumLength)
            {
                problems.Add($"The phone number must be {PhoneNumberMaximumLength} characters or fewer.");
            }
            else if (!LooksLikePhoneNumber(phoneNumber))
            {
                problems.Add("A phone number can contain digits, spaces, and the characters + - ( ) . / # only.");
            }
        }

        return problems;
    }

    /// <summary>
    /// True when this set of details would change nothing about the three stored values. The stored
    /// side is normalized too, so a row written before this page existed (an untrimmed display name
    /// from the staff-creation form, say) does not read as "changed" forever. The e-mail comparison
    /// is case-insensitive because the column is <c>citext</c>.
    /// </summary>
    public bool SameAs(string? storedDisplayName, string? storedEmailAddress, string? storedPhoneNumber)
        => string.Equals(DisplayName, NormalizeText(storedDisplayName), StringComparison.Ordinal)
        && string.Equals(EmailAddress, NormalizeText(storedEmailAddress), StringComparison.OrdinalIgnoreCase)
        && string.Equals(PhoneNumber, NormalizeText(storedPhoneNumber), StringComparison.Ordinal);

    /// <summary>
    /// Trim + internal-whitespace collapse + control-character removal in one pass. A leading
    /// whitespace run appends nothing (the builder is still empty) and a trailing one appends nothing
    /// (there is no following character), which is the trim; a run in the middle appends exactly one
    /// space, which is the collapse.
    /// </summary>
    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        StringBuilder builder = new(value.Length);
        bool pendingSpace = false;

        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (char.IsControl(character))
            {
                // A control character that is not whitespace simply vanishes; it never became a word
                // boundary, so it must not introduce one.
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Exactly one <c>@</c>, something before it, and a dotted domain after it that does not begin or
    /// end with a dot or a hyphen. See the type remarks for why this stops there.
    /// </summary>
    private static bool LooksLikeEmailAddress(string value)
    {
        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> local = value.AsSpan(0, at);
        ReadOnlySpan<char> domain = value.AsSpan(at + 1);

        if (local.Contains(' ') || domain.Contains(' '))
        {
            return false;
        }

        // The shortest plausible domain is a.bc.
        if (domain.Length < 4 || !domain.Contains('.'))
        {
            return false;
        }

        return domain[0] is not ('.' or '-') && domain[^1] is not ('.' or '-');
    }

    /// <summary>
    /// Digits plus the separators people actually type, and at least three digits so a lone "+" or a
    /// pair of brackets does not count as a number.
    /// </summary>
    private static bool LooksLikePhoneNumber(string value)
    {
        int digits = 0;

        foreach (char character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                digits++;
                continue;
            }

            if (character is ' ' or '+' or '-' or '(' or ')' or '.' or '/' or '#')
            {
                continue;
            }

            return false;
        }

        return digits >= 3;
    }
}
