using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// One current, non-removed line of an order, read from the <c>order_current_line</c> view
/// (TECHNICAL_SPECIFICATION §8.3). It is the read-side twin of
/// <see cref="MyRestaurant.Domain.Orders.ProjectedOrderLine"/> and carries one extra field the fold
/// deliberately omits — the menu item's <em>current</em> name, which the view joins at read time
/// (§8.5). The price does not come from the menu: it is the price captured when the line was added,
/// overridden by the latest <c>price_adjustment</c> if one exists (§6.5.4, §6.3).
/// </summary>
public sealed record OrderLineView(
    Guid GuestOrderIdentifier,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    decimal CurrentUnitPriceAmount,
    string? CustomizationNote,
    bool IsFulfilled,
    DateTimeOffset AddedAt,
    Guid AddedByOrderEventIdentifier)
{
    /// <summary>Extended line price at the current unit price (quantity × current unit price).</summary>
    public decimal LineTotalAmount => Quantity * CurrentUnitPriceAmount;
}

/// <summary>
/// One living order's folded state, read from the <c>order_current_state</c> view
/// (TECHNICAL_SPECIFICATION §8.3). The total <em>includes</em> still-pending lines, matching
/// <c>sitting_bill</c> — a guest looking at their running total wants what they have ordered, not what
/// has reached the table.
/// </summary>
public sealed record OrderStateView(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    DateTimeOffset? FirstSubmittedAt,
    DateTimeOffset? LastEventAt,
    int PendingLineCount,
    int FulfilledLineCount,
    decimal CurrentTotalAmount);

/// <summary>
/// One person's share of a sitting's bill, read from the <c>sitting_bill</c> view with the person's
/// names joined on (TECHNICAL_SPECIFICATION §8.3, §11.3). The view is built <em>from</em>
/// <c>guest_order</c>, so it lists people who have an order rather than people who are at the table: a
/// member who joined and never sent anything does not appear at all, while someone whose every line was
/// removed appears with a zero total. The counter's roster of who is present comes from
/// <see cref="Sittings.ISittingDirectory.ListMembersAsync"/>, which is the question that actually asks
/// it.
/// </summary>
public sealed record SittingBillEntry(
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    Guid GuestOrderIdentifier,
    string Username,
    string? DisplayName,
    decimal PersonTotalAmount)
{
    /// <summary>The name to print on the bill: the display name when set, otherwise the username.</summary>
    public string BillName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

/// <summary>
/// One pending line anywhere in the restaurant, read from the <c>kitchen_pending_line</c> view
/// (TECHNICAL_SPECIFICATION §8.3, §11.2). The view already restricts to open sittings and unfulfilled,
/// non-removed lines; this record adds the grouping keys §11.2 orders by (table label → person → order)
/// and resolves the person's name the way the roster does, so a staff account with no display name does
/// not produce a blank ticket header.
/// </summary>
public sealed record KitchenPendingLineView(
    Guid GuestOrderIdentifier,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote,
    DateTimeOffset AddedAt,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    string PersonName,
    Guid TableIdentifier,
    string TableLabel);

/// <summary>
/// The read side of orders (TECHNICAL_SPECIFICATION §8.3, §11.1–§11.3). Every query here goes through
/// the projection views, which are the schema's own statement of what "current" means; the fold in
/// <see cref="MyRestaurant.Domain.Orders.OrderProjection"/> reproduces the same answer from the event
/// log, and §8.5's equivalence test asserts they agree on randomised sequences. Neither is the source
/// of truth — the event tables are (ADR-0002).
///
/// <para>Reads are separated from <see cref="IOrderMutations"/> for the reason every other pair in this
/// layer is (<c>ITableDirectory</c>/<c>ITableAdministration</c>,
/// <c>ISittingDirectory</c>/<c>ISittingMembership</c>): a surface that only renders should not be able
/// to write, and a query needs neither a transaction nor a clock.</para>
/// </summary>
public interface IOrderReadModel
{
    /// <summary>
    /// The living order of one person in one sitting, or <c>null</c> if they have not sent anything yet
    /// (§6.1 — the row is created lazily inside the first send transaction).
    /// </summary>
    Task<Guid?> FindLivingOrderAsync(
        Guid sittingIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>One order's current lines, oldest first — the order the guest's own view renders (§11.1).</summary>
    Task<IReadOnlyList<OrderLineView>> ListLinesForOrderAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>Every current line of every order in one sitting — the "party orders" panel and the counter drill-in (§11.1, §11.3).</summary>
    Task<IReadOnlyList<OrderLineView>> ListLinesForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>One order's folded state, or <c>null</c> when no such order exists.</summary>
    Task<OrderStateView?> GetOrderStateAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>Every order in a sitting, in creation order — the roster of who has ordered what (§11.1, §11.3).</summary>
    Task<IReadOnlyList<OrderStateView>> ListOrderStatesForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>The per-person bill for a sitting, highest total first then by name (§8.3, §11.3).</summary>
    Task<IReadOnlyList<SittingBillEntry>> ListSittingBillAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every pending line in every open sitting, ordered the way the kitchen queue groups them: table
    /// label, then person, then order, then the moment the line was added (§11.2).
    /// </summary>
    Task<IReadOnlyList<KitchenPendingLineView>> ListKitchenPendingLinesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IOrderReadModel"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction, columns aliased to the records'
/// member names, and rows read into internal row types whose members match what Npgsql returns before
/// being projected — <c>timestamptz</c> arrives as <see cref="DateTime"/> and <c>count(*)</c> as
/// <c>bigint</c>, neither of which Dapper's constructor binding will convert, so the counts are cast to
/// <c>int</c> in SQL and the instants are converted here.
/// </summary>
public sealed class DapperOrderReadModel : IOrderReadModel
{
    private const string LineColumns = """
        order_current_line.guest_order_identifier            AS GuestOrderIdentifier,
        order_current_line.order_line_identifier             AS OrderLineIdentifier,
        order_current_line.menu_item_identifier              AS MenuItemIdentifier,
        order_current_line.menu_item_name                    AS MenuItemName,
        order_current_line.quantity                          AS Quantity,
        order_current_line.current_unit_price_amount         AS CurrentUnitPriceAmount,
        order_current_line.customization_note                AS CustomizationNote,
        order_current_line.is_fulfilled                      AS IsFulfilled,
        order_current_line.added_at                          AS AddedAt,
        order_current_line.added_by_order_event_identifier   AS AddedByOrderEventIdentifier
        """;

    private const string StateColumns = """
        order_current_state.guest_order_identifier   AS GuestOrderIdentifier,
        order_current_state.table_sitting_identifier AS SittingIdentifier,
        order_current_state.person_identifier        AS PersonIdentifier,
        order_current_state.first_submitted_at       AS FirstSubmittedAt,
        order_current_state.last_event_at            AS LastEventAt,
        order_current_state.pending_line_count::int  AS PendingLineCount,
        order_current_state.fulfilled_line_count::int AS FulfilledLineCount,
        order_current_state.current_total_amount     AS CurrentTotalAmount
        """;

    private const string FindLivingOrderSql = """
        SELECT guest_order.guest_order_identifier
        FROM guest_order
        WHERE guest_order.table_sitting_identifier = @SittingIdentifier
          AND guest_order.person_identifier = @PersonIdentifier;
        """;

    private static readonly string LinesForOrderSql = $"""
        SELECT {LineColumns}
        FROM order_current_line
        WHERE order_current_line.guest_order_identifier = @GuestOrderIdentifier
        ORDER BY order_current_line.added_at, order_current_line.order_line_identifier;
        """;

    private static readonly string LinesForSittingSql = $"""
        SELECT {LineColumns}
        FROM order_current_line
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = order_current_line.guest_order_identifier
        WHERE guest_order.table_sitting_identifier = @SittingIdentifier
        ORDER BY order_current_line.added_at, order_current_line.order_line_identifier;
        """;

    private static readonly string OrderStateSql = $"""
        SELECT {StateColumns}
        FROM order_current_state
        WHERE order_current_state.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private static readonly string OrderStatesForSittingSql = $"""
        SELECT {StateColumns}
        FROM order_current_state
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = order_current_state.guest_order_identifier
        WHERE order_current_state.table_sitting_identifier = @SittingIdentifier
        ORDER BY guest_order.created_at, order_current_state.guest_order_identifier;
        """;

    private const string SittingBillSql = """
        SELECT sitting_bill.table_sitting_identifier AS SittingIdentifier,
               sitting_bill.person_identifier        AS PersonIdentifier,
               sitting_bill.guest_order_identifier   AS GuestOrderIdentifier,
               person.username                       AS Username,
               person.display_name                   AS DisplayName,
               sitting_bill.person_total_amount      AS PersonTotalAmount
        FROM sitting_bill
        INNER JOIN person
                ON person.person_identifier = sitting_bill.person_identifier
        WHERE sitting_bill.table_sitting_identifier = @SittingIdentifier
        ORDER BY sitting_bill.person_total_amount DESC, person.username;
        """;

    private const string KitchenPendingLinesSql = """
        SELECT kitchen_pending_line.guest_order_identifier          AS GuestOrderIdentifier,
               kitchen_pending_line.order_line_identifier           AS OrderLineIdentifier,
               kitchen_pending_line.menu_item_identifier            AS MenuItemIdentifier,
               kitchen_pending_line.menu_item_name                  AS MenuItemName,
               kitchen_pending_line.quantity                        AS Quantity,
               kitchen_pending_line.customization_note              AS CustomizationNote,
               kitchen_pending_line.added_at                        AS AddedAt,
               kitchen_pending_line.table_sitting_identifier        AS SittingIdentifier,
               kitchen_pending_line.person_identifier               AS PersonIdentifier,
               COALESCE(NULLIF(btrim(kitchen_pending_line.person_display_name), ''), person.username)
                                                                    AS PersonName,
               kitchen_pending_line.restaurant_table_identifier     AS TableIdentifier,
               kitchen_pending_line.restaurant_table_label          AS TableLabel
        FROM kitchen_pending_line
        INNER JOIN person
                ON person.person_identifier = kitchen_pending_line.person_identifier
        ORDER BY kitchen_pending_line.restaurant_table_label,
                 PersonName,
                 kitchen_pending_line.guest_order_identifier,
                 kitchen_pending_line.added_at,
                 kitchen_pending_line.order_line_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperOrderReadModel(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<Guid?> FindLivingOrderAsync(
        Guid sittingIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            FindLivingOrderSql,
            new { SittingIdentifier = sittingIdentifier, PersonIdentifier = personIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OrderLineView>> ListLinesForOrderAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OrderLineRow> rows = await connection.QueryAsync<OrderLineRow>(new CommandDefinition(
            LinesForOrderSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToView).ToArray();
    }

    public async Task<IReadOnlyList<OrderLineView>> ListLinesForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OrderLineRow> rows = await connection.QueryAsync<OrderLineRow>(new CommandDefinition(
            LinesForSittingSql,
            new { SittingIdentifier = sittingIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToView).ToArray();
    }

    public async Task<OrderStateView?> GetOrderStateAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        OrderStateRow? row = await connection.QuerySingleOrDefaultAsync<OrderStateRow>(new CommandDefinition(
            OrderStateSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToView(row);
    }

    public async Task<IReadOnlyList<OrderStateView>> ListOrderStatesForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OrderStateRow> rows = await connection.QueryAsync<OrderStateRow>(new CommandDefinition(
            OrderStatesForSittingSql,
            new { SittingIdentifier = sittingIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToView).ToArray();
    }

    public async Task<IReadOnlyList<SittingBillEntry>> ListSittingBillAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SittingBillEntry> rows = await connection.QueryAsync<SittingBillEntry>(new CommandDefinition(
            SittingBillSql,
            new { SittingIdentifier = sittingIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<KitchenPendingLineView>> ListKitchenPendingLinesAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<KitchenPendingLineRow> rows = await connection.QueryAsync<KitchenPendingLineRow>(new CommandDefinition(
            KitchenPendingLinesSql,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToView).ToArray();
    }

    private static OrderLineView ToView(OrderLineRow row) => new(
        row.GuestOrderIdentifier,
        row.OrderLineIdentifier,
        row.MenuItemIdentifier,
        row.MenuItemName,
        row.Quantity,
        row.CurrentUnitPriceAmount,
        row.CustomizationNote,
        row.IsFulfilled,
        AsUtc(row.AddedAt),
        row.AddedByOrderEventIdentifier);

    private static OrderStateView ToView(OrderStateRow row) => new(
        row.GuestOrderIdentifier,
        row.SittingIdentifier,
        row.PersonIdentifier,
        row.FirstSubmittedAt is { } firstSubmittedAt ? AsUtc(firstSubmittedAt) : null,
        row.LastEventAt is { } lastEventAt ? AsUtc(lastEventAt) : null,
        row.PendingLineCount,
        row.FulfilledLineCount,
        row.CurrentTotalAmount);

    private static KitchenPendingLineView ToView(KitchenPendingLineRow row) => new(
        row.GuestOrderIdentifier,
        row.OrderLineIdentifier,
        row.MenuItemIdentifier,
        row.MenuItemName,
        row.Quantity,
        row.CustomizationNote,
        AsUtc(row.AddedAt),
        row.SittingIdentifier,
        row.PersonIdentifier,
        row.PersonName,
        row.TableIdentifier,
        row.TableLabel);

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record OrderLineRow(
        Guid GuestOrderIdentifier,
        Guid OrderLineIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        int Quantity,
        decimal CurrentUnitPriceAmount,
        string? CustomizationNote,
        bool IsFulfilled,
        DateTime AddedAt,
        Guid AddedByOrderEventIdentifier);

    private sealed record OrderStateRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid PersonIdentifier,
        DateTime? FirstSubmittedAt,
        DateTime? LastEventAt,
        int PendingLineCount,
        int FulfilledLineCount,
        decimal CurrentTotalAmount);

    private sealed record KitchenPendingLineRow(
        Guid GuestOrderIdentifier,
        Guid OrderLineIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        int Quantity,
        string? CustomizationNote,
        DateTime AddedAt,
        Guid SittingIdentifier,
        Guid PersonIdentifier,
        string PersonName,
        Guid TableIdentifier,
        string TableLabel);
}
