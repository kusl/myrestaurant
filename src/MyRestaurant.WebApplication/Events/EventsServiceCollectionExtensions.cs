using MyRestaurant.DataAccess.Events;

namespace MyRestaurant.WebApplication.Events;

/// <summary>
/// Wires the §11.4 event explorer: one reader over the three append-only logs
/// (TECHNICAL_SPECIFICATION §11.4, §8.2).
///
/// <para><b>Why a fifth extension rather than a line in an existing one.</b> Every other group of
/// registrations is a subsystem — identity, tables, displays, orders — and each has resisted being split
/// for a real reason: a host that wired ordering without the reminder loop would alert and never remind,
/// and one that wired ordering without the menu would have a staging area that could list nothing. Both
/// are silent half-failures, so both stay welded together.</para>
///
/// <para>The explorer is not like that. It reads <c>security_event</c> (identity's table),
/// <c>order_event</c> (ordering's) and <c>menu_item_event</c> (the menu's), and belongs to none of the
/// three. Putting it in <see cref="Orders.OrdersServiceCollectionExtensions"/> would make the ordering
/// extension the registrar of a reader of identity's audit log, which is exactly the kind of quiet
/// mis-filing that makes a codebase hard to reason about a year later. And the failure mode of leaving
/// it out is not silent: one administration route throws on resolve, loudly, in front of the person who
/// asked for it. So it gets its own call, and <c>Program.cs</c> gets one line.</para>
///
/// <para>Scoped, like every other data service — it holds no state and opens its own connection per call
/// from the singleton <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>, which
/// <c>Program.cs</c> registers well before this call. There is nothing else to wire: the explorer is
/// read-only by construction, so there is no write service and no post-commit shell to forget.</para>
/// </summary>
public static class EventsServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantEventExplorer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The one cross-cutting reader (§11.4). It writes nothing, ever: the explorer is a window on the
        // three logs, and the only screens that append to them are the ones that own each subsystem.
        services.AddScoped<IEventExplorerReads, DapperEventExplorerReads>();

        return services;
    }
}
