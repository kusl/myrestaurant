using MyRestaurant.DataAccess.Tables;

namespace MyRestaurant.WebApplication.Tables;

/// <summary>
/// Wires the table services (TECHNICAL_SPECIFICATION §4). The management surface (§4.1): the read-only
/// <see cref="ITableDirectory"/> the administration tables list/detail pages read from, and the
/// transactional <see cref="ITableAdministration"/> those pages write through (create, rename, rotate
/// the join secret, deactivate/reactivate). The join-token surface (§4.3–§4.5): the server-only
/// <see cref="ITableJoinSecretReader"/> and the <see cref="ITableJoinTokens"/> service that reads the
/// secret through it to render a table's current rotating QR (the counter/admin fallback, §4.5) and to
/// validate a presented token, recording <c>table_join_tokens_validated_total{result}</c> (§12).
///
/// <para>Kept separate from <c>AddRestaurantIdentity</c> because tables are a §4 concern, not identity;
/// both are registered from <c>Program.cs</c>. Scoped, matching the identity services' lifetime — they
/// hold no state and open their own connection per call from the singleton
/// <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>; their other dependencies
/// (<see cref="MyRestaurant.Domain.Time.IClock"/>, the options, and the metrics) are singletons
/// registered before this call.</para>
/// </summary>
public static class TablesServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantTables(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Management (§4.1).
        services.AddScoped<ITableDirectory, DapperTableDirectory>();
        services.AddScoped<ITableAdministration, DapperTableAdministration>();

        // Join tokens (§4.3–§4.5). The secret reader is the only path to the server-only join secret;
        // the token service is its sole consumer, turning the secret into the rotating QR and the
        // validation outcome.
        services.AddScoped<ITableJoinSecretReader, DapperTableJoinSecretReader>();
        services.AddScoped<ITableJoinTokens, TableJoinTokens>();

        return services;
    }
}
