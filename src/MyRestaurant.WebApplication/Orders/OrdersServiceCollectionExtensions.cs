using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// Wires the ordering services (TECHNICAL_SPECIFICATION §6, §7, §8.3, §9, §10, §12). Four groups:
///
/// <list type="bullet">
///   <item><description><b>Menu, read side (§7)</b> — <see cref="IMenuDirectory"/>, which the guest
///   staging area picks from and the kitchen "86" panel lists. Menu <em>administration</em> (create,
///   rename, reprice, activate/deactivate, each appending a <c>menu_item_event</c>) is M5 and will bring
///   its own write interface and, likely, its own <c>AddRestaurantMenu()</c>; the read side lives here
///   for now because ordering is its only consumer today.</description></item>
///   <item><description><b>The order write path (§6.6)</b> — <see cref="IOrderMutations"/>, the single
///   transaction every order event goes through.</description></item>
///   <item><description><b>The order read side (§8.3)</b> — <see cref="IOrderReadModel"/> over the
///   projection views, and <see cref="IOrderEventLog"/> over the raw event log, which the §8.5
///   equivalence test and the §11.4 event explorer both read.</description></item>
///   <item><description><b>The post-commit shell (§9, §12)</b> — <see cref="IOrderWorkflow"/>, which
///   surfaces call instead of <see cref="IOrderMutations"/> so every committed event is both counted and
///   broadcast.</description></item>
/// </list>
///
/// <para>Registered from <c>Program.cs</c> after <c>AddRestaurantTables()</c>: an order belongs to a
/// sitting, and the surfaces that will consume these services resolve the sitting directory alongside
/// them. Every data service is scoped, matching the identity, table, and display services — they hold no
/// state and open their own connection per call from the singleton
/// <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>; their other dependencies (the clock,
/// the identifier factory, the metrics, and the broadcaster) are singletons registered before this
/// call.</para>
/// </summary>
public static class OrdersServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantOrders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Menu, read side (§7). Deactivated items are returned too — §7 shows them "currently
        // unavailable" rather than letting them vanish.
        services.AddScoped<IMenuDirectory, DapperMenuDirectory>();

        // Orders (§6.6, §8.3, §8.5).
        services.AddScoped<IOrderMutations, DapperOrderMutations>();
        services.AddScoped<IOrderReadModel, DapperOrderReadModel>();
        services.AddScoped<IOrderEventLog, DapperOrderEventLog>();

        // The post-commit shell surfaces actually call (§9, §12).
        services.AddScoped<IOrderWorkflow, OrderWorkflow>();

        return services;
    }
}
