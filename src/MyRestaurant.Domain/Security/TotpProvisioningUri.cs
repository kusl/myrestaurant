namespace MyRestaurant.Domain.Security;

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

        return $"otpauth://totp/{encodedIssuer}:{encodedUsername}?secret={encodedSecret}&issuer={encodedIssuer}";
    }
}
