namespace MyRestaurant.WebApplication.Authorization;

public static class RestaurantRoles
{
    public const string Administrator = "administrator";
    public const string Kitchen = "kitchen";
    public const string Counter = "counter";
}

public static class AuthorizationPolicies
{
    public const string Table = "area.table";

    public const string Kitchen = "area.kitchen";

    public const string Counter = "area.counter";

    public const string Administration = "area.administration";
}

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
