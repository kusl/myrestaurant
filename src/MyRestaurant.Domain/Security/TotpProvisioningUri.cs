namespace MyRestaurant.Domain.Security;

/// <summary>
/// Builds the <c>otpauth://totp/…</c> provisioning URI an authenticator app imports
/// (TECHNICAL_SPECIFICATION §3.4). The label is <c>{issuer}:{username}</c> and the issuer is
/// repeated as the <c>issuer</c> parameter, per the Key Uri Format that Google Authenticator and
/// compatible apps expect; the issuer is the configured <c>RESTAURANT_NAME</c> (§13). The secret is
/// the app's Base32 authenticator key. Algorithm/digits/period are left implicit — they equal the
/// RFC 6238 defaults this app uses (SHA-1, 6 digits, 30 s), and emitting them changes nothing while
/// tripping bugs in a few readers.
///
/// <para>Every user- or configuration-supplied component is percent-encoded with
/// <see cref="Uri.EscapeDataString(string)"/>, so a restaurant name or username containing a space,
/// <c>':'</c>, <c>'&amp;'</c>, or non-ASCII character cannot break out of its field.</para>
/// </summary>
public static class TotpProvisioningUri
{
    public static string Build(string issuer, string username, string base32Secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(base32Secret);

        string encodedIssuer = Uri.EscapeDataString(issuer);
        string encodedUsername = Uri.EscapeDataString(username);
        string encodedSecret = Uri.EscapeDataString(base32Secret);

        // Label: "{issuer}:{username}", each component escaped, the ':' a literal separator.
        return $"otpauth://totp/{encodedIssuer}:{encodedUsername}?secret={encodedSecret}&issuer={encodedIssuer}";
    }
}
