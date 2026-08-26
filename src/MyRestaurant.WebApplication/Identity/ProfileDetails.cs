using System.Text;

namespace MyRestaurant.WebApplication.Identity;

public sealed record ProfileDetails(string? DisplayName, string? EmailAddress, string? PhoneNumber)
{
    public const int DisplayNameMaximumLength = 120;

    public const int EmailAddressMaximumLength = 254;

    public const int PhoneNumberMaximumLength = 32;

    public static ProfileDetails Normalize(string? displayName, string? emailAddress, string? phoneNumber)
        => new(NormalizeText(displayName), NormalizeText(emailAddress), NormalizeText(phoneNumber));

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

    public bool SameAs(string? storedDisplayName, string? storedEmailAddress, string? storedPhoneNumber)
        => string.Equals(DisplayName, NormalizeText(storedDisplayName), StringComparison.Ordinal)
        && string.Equals(EmailAddress, NormalizeText(storedEmailAddress), StringComparison.OrdinalIgnoreCase)
        && string.Equals(PhoneNumber, NormalizeText(storedPhoneNumber), StringComparison.Ordinal);

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

        if (domain.Length < 4 || !domain.Contains('.'))
        {
            return false;
        }

        return domain[0] is not ('.' or '-') && domain[^1] is not ('.' or '-');
    }

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
