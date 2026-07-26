namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// The two halves of a parsed display-device cookie: which device claims to be calling, and the secret
/// it presents as proof (TECHNICAL_SPECIFICATION §4.2). Holding them apart is the point — the identifier
/// selects the row, the secret is compared against that row's stored SHA-256 hash in constant time.
/// </summary>
/// <param name="DeviceIdentifier">The <c>table_display_device</c> primary key from the cookie.</param>
/// <param name="Secret">The Base64Url secret, exactly as it travels; never decoded, only hashed.</param>
public sealed record DisplayDeviceCookieValue(Guid DeviceIdentifier, string Secret);

/// <summary>
/// The display device's long-lived credential cookie (TECHNICAL_SPECIFICATION §4.2). §4.2 specifies the
/// value verbatim — <c>device:{device_identifier}:{secret}</c>, where the secret is 32 random bytes in
/// Base64Url — and the cookie's shape: Secure, HttpOnly, SameSite=Lax, expiry ~365 days.
///
/// <para>Unlike the join grant (§4.4), this value is <b>not</b> Data-Protection-encrypted, and that is
/// deliberate rather than an omission: the grant is a self-describing capability that the server must be
/// able to trust on sight, so it has to be sealed, whereas this is a bearer secret checked against a
/// database row on every request. Encrypting it would add nothing — an attacker who has the cookie has
/// the credential either way — and it would couple a 365-day credential to the Data Protection key ring,
/// so a key-ring rotation would silently unpair every screen in the restaurant. The credential is
/// revocable instead (§4.2), which is the property that actually matters.</para>
///
/// <para>A year-long cookie is safe here for the same reason §17 lists display theft as mitigated: the
/// device holds nothing worth extracting. The table's join secret never leaves the server, and the only
/// thing the screen ever displayed was a token that expires within ~120 seconds.</para>
/// </summary>
public static class DisplayDeviceCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "myrestaurant.display";

    /// <summary>The literal first segment of the value, per §4.2's <c>device:{id}:{secret}</c>.</summary>
    public const string ValuePrefix = "device";

    /// <summary>§4.2: "expiry ~365 days".</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>Composes the cookie value for a freshly paired device (§4.2).</summary>
    public static string Format(Guid deviceIdentifier, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return $"{ValuePrefix}:{deviceIdentifier:D}:{secret}";
    }

    /// <summary>
    /// Parses a presented cookie value. Returns <c>false</c> (never throws) for anything that is not
    /// exactly the §4.2 shape — wrong prefix, unparseable or all-zero identifier, empty secret, or extra
    /// colons — so the middleware can treat a mangled cookie as simply "no device". Base64Url contains no
    /// colon, so a well-formed value always splits into exactly three parts.
    /// </summary>
    public static bool TryParse(string? rawValue, out DisplayDeviceCookieValue? value)
    {
        value = null;
        if (string.IsNullOrEmpty(rawValue))
        {
            return false;
        }

        string[] parts = rawValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], ValuePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParse(parts[1], out Guid deviceIdentifier) || deviceIdentifier == Guid.Empty)
        {
            return false;
        }

        if (parts[2].Length == 0)
        {
            return false;
        }

        value = new DisplayDeviceCookieValue(deviceIdentifier, parts[2]);
        return true;
    }

    /// <summary>
    /// True when the request carried a display cookie at all, whatever its state. The pairing page uses
    /// this to tell "this screen was disconnected" (§11.5) from "this screen has never been paired": the
    /// middleware clears a credential it could not honour, but clearing sets a response header — the
    /// request's own copy is still visible here.
    /// </summary>
    public static bool WasPresented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.ContainsKey(Name);
    }

    /// <summary>Reads and parses the cookie from a request, if it carries a well-formed one.</summary>
    public static bool TryRead(HttpRequest request, out DisplayDeviceCookieValue? value)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryParse(request.Cookies[Name], out value);
    }

    /// <summary>Writes (or replaces) the credential cookie on the response — pairing's last step (§4.2).</summary>
    public static void Write(HttpResponse response, Guid deviceIdentifier, string secret)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, Format(deviceIdentifier, secret), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            MaxAge = Lifetime,
        });
    }

    /// <summary>
    /// Clears the credential — on revocation, or when the value presented is not one we can honour. The
    /// options must match those the cookie was written with or the browser keeps the original.
    /// </summary>
    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
        });
    }
}
