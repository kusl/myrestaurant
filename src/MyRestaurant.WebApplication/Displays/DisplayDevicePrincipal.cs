using System.Security.Claims;
using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

public static class DisplayDeviceClaimTypes
{
    public const string PrincipalKind = "myrestaurant:principal_kind";

    public const string TableIdentifier = "myrestaurant:display_table";

    public const string TableLabel = "myrestaurant:display_table_label";
}

public static class DisplayDevicePrincipal
{
    public const string AuthenticationType = "MyRestaurant.DisplayDevice";

    public const string PrincipalKind = "table_display";

    public static ClaimsPrincipal Create(DisplayDeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, session.DeviceIdentifier.ToString("D")),
            new Claim(ClaimTypes.Name, session.DeviceLabel),
            new Claim(DisplayDeviceClaimTypes.PrincipalKind, PrincipalKind),
            new Claim(DisplayDeviceClaimTypes.TableIdentifier, session.TableIdentifier.ToString("D")),
            new Claim(DisplayDeviceClaimTypes.TableLabel, session.TableLabel),
        ];

        ClaimsIdentity identity = new(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    public static bool IsDisplayDevice(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
        && string.Equals(
            principal.FindFirstValue(DisplayDeviceClaimTypes.PrincipalKind),
            PrincipalKind,
            StringComparison.Ordinal);

    public static Guid? DeviceIdentifierFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? ParseIdentifier(principal!.FindFirstValue(ClaimTypes.NameIdentifier))
            : null;

    public static Guid? TableIdentifierFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? ParseIdentifier(principal!.FindFirstValue(DisplayDeviceClaimTypes.TableIdentifier))
            : null;

    public static string? TableLabelFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? principal!.FindFirstValue(DisplayDeviceClaimTypes.TableLabel)
            : null;

    public static string? DeviceLabelFor(ClaimsPrincipal? principal)
        => IsDisplayDevice(principal)
            ? principal!.FindFirstValue(ClaimTypes.Name)
            : null;

    private static Guid? ParseIdentifier(string? value)
        => Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty ? identifier : null;
}
