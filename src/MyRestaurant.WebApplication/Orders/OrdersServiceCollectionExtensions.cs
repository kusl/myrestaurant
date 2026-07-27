using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.WebApplication.Menu;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// Wires the ordering services (TECHNICAL_SPECIFICATION §6, §7, §8.3, §8.4, §9, §10, §12). Five groups:
///
/// <list type="bullet">
///   <item><description><b>Menu (§7)</b> — <see cref="IMenuDirectory"/>, which the guest staging area
///   picks from and the kitchen "86" panel lists; and <see cref="IMenuAvailability"/> with its
///   post-commit shell <see cref="IMenuWorkflow"/>, which is the "86" toggle itself (§11.2). The rest of
///   §11.4's menu CRUD — create, rename, reprice, per-item event history — is M5 and will grow from
///   these rather than replace them.</description></item>
///   <item><description><b>The order write path (§6.6)</b> — <see cref="IOrderMutations"/>, the single
///   transaction every order event goes through.</description></item>
///   <item><description><b>The order read side (§8.3, §11.2)</b> — <see cref="IOrderReadModel"/> over
///   the projection views, <see cref="IOrderEventLog"/> over the raw event log (read by the §8.5
///   equivalence test and the §11.4 event explorer), and <see cref="IKitchenBoardReads"/>, the kitchen
///   board's recently-fulfilled query behind its Undo control.</description></item>
///   <item><description><b>The post-commit shell (§9, §12)</b> — <see cref="IOrderWorkflow"/>, which
///   surfaces call instead of <see cref="IOrderMutations"/> so every committed event is both counted and
///   broadcast.</description></item>
///   <item><description><b>Kitchen alerting (§8.4, §10.2)</b> — <see cref="IKitchenNotifications"/> and
///   the <see cref="KitchenReminderService"/> hosted service that drives it.</description></item>
/// </list>
///
/// <para><b>Yes, this registers a hosted service.</b> <see cref="KitchenReminderService"/> is the §10.2
/// reminder loop, and it is registered here rather than in <c>Program.cs</c> because it is not a
/// free-standing background job — it is the second half of a rule whose first half (§10.1's initial
/// alert) is already inside <see cref="IOrderMutations"/>'s transaction. Splitting the two across two
/// files would make it possible to wire ordering into a host and get a system that alerts but never
/// reminds, which is a silent failure. It depends on <see cref="Configuration.RestaurantOptions"/> and
/// <see cref="Observability.RestaurantMetrics"/>, both of which <c>Program.cs</c> registers before this
/// call.</para>
///
/// <para>Registered from <c>Program.cs</c> after <c>AddRestaurantTables()</c>: an order belongs to a
/// sitting, and the surfaces that consume these services resolve the sitting directory alongside them.
/// Every data service is scoped, matching the identity, table, and display services — they hold no state
/// and open their own connection per call from the singleton
/// <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>; their other dependencies (the clock,
/// the identifier factory, the metrics, and the broadcaster) are singletons registered before this
/// call.</para>
/// </summary>
public static class OrdersServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantOrders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Menu (§7). The directory returns deactivated items too — §7 shows them "currently
        // unavailable" rather than letting them vanish. The availability write is the kitchen's "86"
        // (§11.2); surfaces take the workflow, never the raw write, or an 86'd item would stay
        // selectable in every open guest picker until its page happened to reload (§9).
        services.AddScoped<IMenuDirectory, DapperMenuDirectory>();
        services.AddScoped<IMenuAvailability, DapperMenuAvailability>();
        services.AddScoped<IMenuWorkflow, MenuAvailabilityWorkflow>();

        // Orders (§6.6, §8.3, §8.5, §11.2).
        services.AddScoped<IOrderMutations, DapperOrderMutations>();
        services.AddScoped<IOrderReadModel, DapperOrderReadModel>();
        services.AddScoped<IOrderEventLog, DapperOrderEventLog>();
        services.AddScoped<IKitchenBoardReads, DapperKitchenBoardReads>();

        // The post-commit shell surfaces actually call (§9, §12).
        services.AddScoped<IOrderWorkflow, OrderWorkflow>();

        // Kitchen alerting, reminder half (§8.4, §10.2). The initial alert is not here: §10.1 requires
        // its row to be written inside the order transaction, so it lives in DapperOrderMutations.
        services.AddScoped<IKitchenNotifications, DapperKitchenNotifications>();
        services.AddHostedService<KitchenReminderService>();

        return services;
    }
}
