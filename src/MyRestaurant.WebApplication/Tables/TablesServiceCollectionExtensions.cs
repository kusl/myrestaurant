using MyRestaurant.DataAccess.Tables;

namespace MyRestaurant.WebApplication.Tables;

/// <summary>
/// Wires the table-management services (TECHNICAL_SPECIFICATION §4.1): the read-only
/// <see cref="ITableDirectory"/> the administration tables list/detail pages read from, and the
/// transactional <see cref="ITableAdministration"/> those pages write through (create, rename, rotate
/// the join secret, deactivate/reactivate).
///
/// <para>Kept separate from <c>AddRestaurantIdentity</c> because tables are a §4 concern, not identity;
/// both are registered from <c>Program.cs</c>. Scoped, matching the identity services' lifetime — they
/// hold no state and open their own connection per call from the singleton
/// <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>; their only other dependencies
/// (<see cref="MyRestaurant.Domain.Time.IClock"/>) are singletons registered before this call.</para>
/// </summary>
public static class TablesServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantTables(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITableDirectory, DapperTableDirectory>();
        services.AddScoped<ITableAdministration, DapperTableAdministration>();

        return services;
    }
}
