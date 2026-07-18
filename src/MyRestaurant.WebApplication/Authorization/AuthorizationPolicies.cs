namespace MyRestaurant.WebApplication.Authorization;

/// <summary>
/// The three stored role names (TECHNICAL_SPECIFICATION §3.7), spelled exactly as the
/// <c>person_role.role_name</c> CHECK stores them — lower case. The Dapper role store returns roles in
/// this casing, the claims factory turns each into a role claim verbatim, and the policies below match
/// on it, so the whole chain agrees on one spelling. <c>guest</c> is not here: it is the implicit
/// capacity of any authenticated person on their own order, never a stored role. <c>table_display</c>
/// is a device principal, not a person role.
/// </summary>
public static class RestaurantRoles
{
    public const string Administrator = "administrator";
    public const string Kitchen = "kitchen";
    public const string Counter = "counter";
}

/// <summary>
/// Named authorization policies for the routed areas (TECHNICAL_SPECIFICATION §3.7). The policies are
/// registered here; pages attach them with <c>[Authorize(Policy = ...)]</c> as each area is built in
/// later milestones. The display-device policies (<c>/display/{table}</c>, <c>/display/pair</c>) are a
/// device-principal concern and arrive with M3.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary><c>/table</c> — any authenticated person (per-sitting membership is checked separately).</summary>
    public const string Table = "area.table";

    /// <summary><c>/kitchen</c> — the kitchen or administrator role.</summary>
    public const string Kitchen = "area.kitchen";

    /// <summary><c>/counter</c> — the counter or administrator role.</summary>
    public const string Counter = "area.counter";

    /// <summary><c>/administration</c> — the administrator role.</summary>
    public const string Administration = "area.administration";
}

/// <summary>Registers the area authorization policies (§3.7).</summary>
public static class AuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Table, policy => policy
                .RequireAuthenticatedUser())
            .AddPolicy(AuthorizationPolicies.Kitchen, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RestaurantRoles.Kitchen, RestaurantRoles.Administrator))
            .AddPolicy(AuthorizationPolicies.Counter, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RestaurantRoles.Counter, RestaurantRoles.Administrator))
            .AddPolicy(AuthorizationPolicies.Administration, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RestaurantRoles.Administrator));

        return services;
    }
}
