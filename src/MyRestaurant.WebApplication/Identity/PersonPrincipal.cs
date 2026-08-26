using System.Security.Claims;

namespace MyRestaurant.WebApplication.Identity;

public static class PersonPrincipal
{
    public static Guid? IdentifierFor(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
            ? ParseIdentifier(principal.FindFirstValue(ClaimTypes.NameIdentifier))
            : null;

    public static Guid? ParseIdentifier(string? value)
        => Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty ? identifier : null;
}
