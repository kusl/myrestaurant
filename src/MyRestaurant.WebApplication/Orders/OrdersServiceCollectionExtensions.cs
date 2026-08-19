using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.WebApplication.Menu;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// Wires the ordering services (TECHNICAL_SPECIFICATION §6, §7, §8.3, §8.4, §9, §10, §12). Six groups:
///
/// <list type="bullet">
///   <item><description><b>Menu (§7, §11.4)</b> — <see cref="IMenuDirectory"/>, which the guest staging
///   area picks from and the kitchen "86" panel lists; <see cref="IMenuAvailability"/>, the 86 toggle
///   itself (§11.2); <see cref="IMenuAdministration"/>, the administrator's create/rename/reprice
///   (§11.4); <see cref="IMenuEventLog"/>, which reads the append-only history both writes leave behind;
///   and <see cref="IMenuWorkflow"/>, the one post-commit shell over both write services, which is what
///   every surface takes.</description></item>
///   <item><description><b>The order write path (§6.6)</b> — <see cref="IOrderMutations"/>, the single
///   transaction every order event goes through.</description></item>
///   <item><description><b>The order read side (§8.3, §11.2)</b> — <see cref="IOrderReadModel"/> over
///   the projection views, <see cref="IOrderEventLog"/> over the raw event log (read by the §8.5
///   equivalence test and the §11.4 event explorer), and <see cref="IKitchenBoardReads"/>, the kitchen
///   board's recently-fulfilled query behind its Undo control.</description></item>
///   <item><description><b>The post-commit shell (§9, §12)</b> — <see cref="IOrderWorkflow"/>, which
///   surfaces call instead of <see cref="IOrderMutations"/> so every committed event is both counted and
///   broadcast.</description></item>
///   <item><description><b>Visibility (§6.8, §11.1, §11.4)</b> — <see cref="IOrderHistoryReads"/>, which
///   answers the two person-scoped questions no other reader can ("which of my past orders may I still
///   see", "what has been hidden") plus the visibility log behind both;
///   <see cref="IOrderVisibility"/>, the owner-hide and administrator-unhide transaction; and
///   <see cref="IOrderVisibilityWorkflow"/>, the post-commit shell that announces the change, which is
///   what both surfaces take.</description></item>
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
/// <para><b>Yes, menu administration is registered from the orders extension.</b> The menu is not an
/// ordering concern, but it is not a table or an identity concern either, and it has been wired here
/// since M4 for the reason the guest picker exists: an order prices itself from the menu (§6.5.4), so
/// nothing that can take an order can be wired without it. Adding a fifth <c>AddRestaurantMenu()</c>
/// would mean a host could register ordering and get a system whose staging area cannot list anything —
/// the same class of half-wired failure as the reminder loop above. <c>Program.cs</c> therefore needs no
/// edit for the M5 menu slice.</para>
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

        // Menu (§7, §11.4). The directory returns deactivated items too — §7 shows them "currently
        // unavailable" rather than letting them vanish. Two write services, split by audience rather
        // than by table: availability is the kitchen's and the counter's 86 (§11.2), while create,
        // rename, and reprice are administrator-only (§11.4). Surfaces take the workflow, never either
        // raw write, or a changed menu would stay stale in every open guest picker until its page
        // happened to reload (§9) — and a guest would then be quoted a price nobody charges, or have a
        // whole send refused for an item that vanished under them (§6.5.9).
        services.AddScoped<IMenuDirectory, DapperMenuDirectory>();
        services.AddScoped<IMenuAvailability, DapperMenuAvailability>();
        services.AddScoped<IMenuAdministration, DapperMenuAdministration>();
        services.AddScoped<IMenuEventLog, DapperMenuEventLog>();
        services.AddScoped<IMenuWorkflow, MenuWorkflow>();

        // Menu sections (§7, §11.4). Registered here rather than in a group of their own: they are the
        // same table family, the same audience, and the same lifetime, and a second
        // AddRestaurantMenuSections() would be a fifth call the composition root has to remember (see the
        // note above about why this method is not split).
        //
        // ALL FIVE section writes are behind IMenuWorkflow now, and the obligation carried since Slice 37
        // is closed. The rule never changed: a workflow verb with no caller is a code path no test can
        // reach through the interface it is supposed to protect, so a verb arrives when its surface does.
        // The create page brought CreateMenuSectionAsync in with 0005; the section editor brings rename,
        // describe, reorder and set-active in together, because they are four forms on one page.
        //
        // IMenuSectionAdministration is still registered by name because MenuWorkflow takes it as a
        // dependency — what changed is that no SURFACE resolves it any more, exactly as no surface
        // resolves IMenuAdministration or IMenuAvailability. Anything under Components/ that reaches for
        // one of the three raw write services is a page that can change the menu without telling anybody,
        // and §9 is the whole reason that is a defect rather than a style.
        //
        // The section event log is a read and joins the read side: §11.4 renders a heading's complete
        // uncapped history on its own page, which is the one thing the editor could not have been shipped
        // without.
        services.AddScoped<IMenuSectionDirectory, DapperMenuSectionDirectory>();
        services.AddScoped<IMenuSectionAdministration, DapperMenuSectionAdministration>();
        services.AddScoped<IMenuSectionEventLog, DapperMenuSectionEventLog>();

        // Menu item images (§7, §8.2, and Stage 4a of docs/MENU_AND_HANDHELD_PLAN.md). Same table
        // family, same audience, same lifetime, so they are registered here for the reason the section
        // services are rather than behind a fifth call the composition root has to remember.
        //
        // BOTH HAVE CALLERS AS OF STAGE 4b, AND THE OBLIGATION SLICE 51 RE-OPENED IS DISCHARGED.
        // The write is behind IMenuWorkflow like every other menu write, so no surface resolves it
        // directly — the same standing IMenuAdministration, IMenuAvailability and
        // IMenuSectionAdministration have. The DIRECTORY is resolved by surfaces, and legitimately: it is
        // a read, and reads are taken straight (IMenuDirectory and IMenuSectionDirectory both are). Its
        // three methods do not all have one yet — ListAsync is §11.1's, which Stage 4c builds — and that
        // is a read with no caller rather than a write with no caller, which is the weaker of the two:
        // an unread read cannot change anything without telling anybody.
        //
        // The route that serves the bytes is NOT registered here. It is an endpoint rather than a
        // service, mapped from Program.cs beside the clock and the account endpoints
        // (MenuItemImageEndpoints.MapRestaurantMenuImages), and it resolves IMenuItemImageDirectory out
        // of the request scope this call creates.
        services.AddScoped<IMenuItemImageDirectory, DapperMenuItemImageDirectory>();
        services.AddScoped<IMenuItemImageAdministration, DapperMenuItemImageAdministration>();

        // Orders (§6.6, §8.3, §8.5, §11.2).
        services.AddScoped<IOrderMutations, DapperOrderMutations>();
        services.AddScoped<IOrderReadModel, DapperOrderReadModel>();
        services.AddScoped<IOrderEventLog, DapperOrderEventLog>();
        services.AddScoped<IKitchenBoardReads, DapperKitchenBoardReads>();

        // The post-commit shell surfaces actually call (§9, §12).
        services.AddScoped<IOrderWorkflow, OrderWorkflow>();

        // Visibility (§6.8). The reads enforce hiding in SQL for every person-scoped query, so no surface
        // can forget the filter; the write service is the only path to an order_visibility_event row, and
        // the workflow above it is what the guest's history page and the administration hidden-records
        // page take — a hide nobody announced (§9) leaves the row on every other phone the guest has the
        // page open on, which is precisely the moment they are watching for it to disappear.
        services.AddScoped<IOrderHistoryReads, DapperOrderHistoryReads>();
        services.AddScoped<IOrderVisibility, DapperOrderVisibility>();
        services.AddScoped<IOrderVisibilityWorkflow, OrderVisibilityWorkflow>();

        // Kitchen alerting, reminder half (§8.4, §10.2). The initial alert is not here: §10.1 requires
        // its row to be written inside the order transaction, so it lives in DapperOrderMutations.
        services.AddScoped<IKitchenNotifications, DapperKitchenNotifications>();
        services.AddHostedService<KitchenReminderService>();

        return services;
    }
}
