using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Orders;

namespace MyRestaurant.DataAccess.Sittings;

/// <summary>
/// One stored operation of one order event, exactly as the five typed operation tables hold it
/// (TECHNICAL_SPECIFICATION §6.3, §8.2, §11.4).
///
/// <para><see cref="OperationKind"/> is the query-local discriminator from
/// <c>OrderEventVocabulary</c> rather than a C# type hierarchy, and <see cref="StoredOrderEvent"/>'s
/// <c>EventType</c> and <c>ActorRole</c> are the stored strings rather than enums. That is the same
/// choice <see cref="Menu.MenuItemEventEntry"/> made and for the same reason: §11.4 requires
/// administration to render "the complete stored record … never projected or truncated", and an enum is
/// a projection with a failure mode — a value this build does not know about would either throw or be
/// silently mapped to something wrong, and the one reader whose job is to show what is actually in the
/// table is the last place that may happen. <see cref="Orders.IOrderEventLog"/> does map to the domain
/// enums, because its consumers are the fold and the validator, which must refuse to proceed on a word
/// they do not understand. A screen must not.</para>
///
/// <para><see cref="MenuItemName"/> and <see cref="Quantity"/> are present on <em>every</em> kind, not
/// just <c>line_added</c>: they are read-time joins back through
/// <c>order_operation_line_added.order_line_identifier</c> (NOT NULL UNIQUE, and the FK target of the
/// other four tables, so the join is exact and total). Without them a removal reads "removed
/// 0192f0…" and the record is unreadable by the one person it exists for. The name is the item's name
/// <em>now</em>, matching every other surface (§8.3); <see cref="UnitPriceAmount"/> and
/// <see cref="NewUnitPriceAmount"/> are not joined and stay null off their own kinds, because a price is
/// the thing arguments are about and this record must not invent one.</para>
/// </summary>
/// <param name="OperationIdentifier">The operation row's own UUIDv7 primary key (ADR-0011).</param>
/// <param name="OrderEventIdentifier">The event that owns it.</param>
/// <param name="OperationKind">Which of the five tables it came from: <c>line_added</c>, <c>line_removed</c>, <c>line_price_adjusted</c>, <c>line_fulfilled</c>, <c>line_fulfillment_reverted</c>.</param>
/// <param name="OrderLineIdentifier">The line it concerns (§6.4).</param>
/// <param name="MenuItemIdentifier">The item that line is of — joined for every kind.</param>
/// <param name="MenuItemName">That item's current name — joined for every kind.</param>
/// <param name="Quantity">The quantity the line was added with — joined for every kind.</param>
/// <param name="UnitPriceAmount">The price captured when the line was added; <c>null</c> off <c>line_added</c>.</param>
/// <param name="CustomizationNote">The note the line was added with; <c>null</c> off <c>line_added</c>.</param>
/// <param name="NewUnitPriceAmount">The price an adjustment set; <c>null</c> off <c>line_price_adjusted</c>.</param>
/// <param name="Reason">A removal's optional reason or an adjustment's required one; <c>null</c> off both.</param>
public sealed record StoredOrderOperation(
    Guid OperationIdentifier,
    Guid OrderEventIdentifier,
    string OperationKind,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    decimal? UnitPriceAmount,
    string? CustomizationNote,
    decimal? NewUnitPriceAmount,
    string? Reason);

/// <summary>
/// One event of one order's append-only log, with its actor named and its operations attached
/// (TECHNICAL_SPECIFICATION §6.2, §8.2, §11.4).
/// </summary>
/// <param name="OrderEventIdentifier">The event's UUIDv7 primary key (ADR-0011).</param>
/// <param name="GuestOrderIdentifier">The order it was appended to.</param>
/// <param name="SequenceNumber">Its per-order monotonic position, assigned under the order lock (§6.6).</param>
/// <param name="EventType">The stored <c>order_event.event_type</c>.</param>
/// <param name="ActorPersonIdentifier">Who authored it.</param>
/// <param name="ActorName">Their display name, falling back to their username — the rendering rule every staff surface uses.</param>
/// <param name="ActorRole">The stored <c>order_event.actor_role</c> — the capacity they acted in, which for a guest is not a stored role at all (§0, §3.7).</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
/// <param name="Operations">Its operations, deterministically ordered.</param>
public sealed record StoredOrderEvent(
    Guid OrderEventIdentifier,
    Guid GuestOrderIdentifier,
    long SequenceNumber,
    string EventType,
    Guid ActorPersonIdentifier,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    IReadOnlyList<StoredOrderOperation> Operations);

/// <summary>
/// One living order in a sitting and everything that has ever happened to it
/// (TECHNICAL_SPECIFICATION §6.1, §11.4).
/// </summary>
/// <param name="GuestOrderIdentifier">The order's UUIDv7 primary key (ADR-0011).</param>
/// <param name="SittingIdentifier">The sitting it belongs to.</param>
/// <param name="PersonIdentifier">Whose order it is.</param>
/// <param name="Username">That person's username.</param>
/// <param name="DisplayName">Their display name, when they have set one.</param>
/// <param name="CreatedAt">When the first send created the row (§6.1 — lazily, inside that transaction).</param>
/// <param name="Events">Every event on it, ascending by <see cref="StoredOrderEvent.SequenceNumber"/>.</param>
public sealed record SittingOrderRecord(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StoredOrderEvent> Events)
{
    /// <summary>The name to head this order with: the display name when set, otherwise the username.</summary>
    public string OwnerName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

/// <summary>
/// Reads a whole sitting's orders as the complete stored record administration renders
/// (TECHNICAL_SPECIFICATION §11.4: "Administration renders the <b>complete stored record</b> everywhere
/// — full event streams … never projected or truncated for the administrator").
///
/// <para><b>Why this exists beside <see cref="Orders.IOrderEventLog"/>.</b> That interface already reads
/// one order's log, and it is the right reader for the two callers it has: the §6.5 validator inside the
/// order transaction, and the §8.5 equivalence test. Both want domain
/// <see cref="MyRestaurant.Domain.Orders.OrderEvent"/>s and both must throw on a word they do not
/// recognise. Neither wants an actor's name, a menu item's name, or a way to ask the question by sitting
/// — and adding those to it would put a rendering concern inside the type the validator folds. So this
/// is the third reader of the same tables, in the same relationship
/// <see cref="ICounterBoardReads"/> has to <see cref="ISittingDirectory"/>: a different question, asked
/// by a different audience, kept out of the type that answers the first one.</para>
///
/// <para><b>Nothing here is filtered, capped, or paged.</b> The whole value of the record is that it is
/// the record: a removed line, a reverted fulfillment, and a superseded price are all still in it,
/// because the event log outliving the state is the entire point of ADR-0002. A sitting has at most a
/// party's worth of orders and a service's worth of events, so the honest read is the complete one.</para>
/// </summary>
public interface ISittingRecordReads
{
    /// <summary>
    /// Every order in one sitting, in creation order, each carrying its complete event log oldest first.
    /// A sitting nobody ordered in, and an identifier no sitting has, both yield an empty list — §6.1
    /// creates the order row lazily, so "no orders yet" and "no sitting" are the same answer to a
    /// reader, and the page above says which it is from the sitting header it already has.
    /// </summary>
    Task<IReadOnlyList<SittingOrderRecord>> ListOrderRecordsForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One order's complete record, or <c>null</c> when no order has that identifier — the same answer
    /// <see cref="ListOrderRecordsForSittingAsync"/> gives, asked one order at a time.
    ///
    /// <para>It exists for §6.8's hidden-records view (§11.4), which lists orders from many different
    /// sittings and expands one of them. Reading the whole sitting to render one order would show an
    /// administrator the rest of that party's orders as a side effect of opening a row about one person's
    /// hidden meal — which is not what they asked for, and §11.4 is explicit that filters narrow "only on
    /// explicit request" in <em>both</em> directions.</para>
    ///
    /// <para>Unlike the sitting-scoped question, "no such order" is distinguishable from "an order with
    /// no events" here: the first returns <c>null</c>, the second a record whose <c>Events</c> is empty.
    /// The caller has an identifier it got from somewhere, so a page that finds nothing can say the link
    /// is stale rather than that the meal was never eaten.</para>
    /// </summary>
    Task<SittingOrderRecord?> GetOrderRecordAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ISittingRecordReads"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns
/// aliased to the records' member names, every column reference table-qualified — <c>guest_order</c>,
/// <c>order_event</c>, and all five operation tables carry same-named identifier columns, and an
/// unqualified reference to one is exactly how PostgreSQL error 42702 bites — and rows read into
/// internal row types whose members match what Npgsql actually returns before being projected, because
/// <c>timestamptz</c> arrives as <see cref="DateTime"/> and Dapper's constructor binding will not feed
/// one into a <see cref="DateTimeOffset"/> parameter.
///
/// <para><b>Three queries, not one per order.</b> The orders, then every event across all of them, then
/// every operation across all of them, grouped in memory. A party of six with a long service is three
/// round trips rather than thirteen, and the query count does not move with the size of the party —
/// which matters because this page is the one somebody opens while a guest is standing in front of
/// them.</para>
/// </summary>
public sealed class DapperSittingRecordReads : ISittingRecordReads
{
    /// <summary>
    /// The two questions the three queries below are asked, as WHERE fragments. Both are applied to a
    /// <c>guest_order</c> row — either the alias <c>guest_order</c> itself (the first two queries) or the
    /// per-branch alias the operations union gives it — so one set of SQL serves both scopes and neither
    /// can drift from the other.
    ///
    /// <para>Fragments rather than a composed string: nothing here is derived from input, both are
    /// <c>const</c>, and the parameter placeholders stay parameters. The point is not to build SQL
    /// dynamically but to stop the same 180 lines of union existing twice, which is how a reader ends up
    /// fixed in one copy and wrong in the other.</para>
    /// </summary>
    private const string SittingScopeColumn = "table_sitting_identifier = @SittingIdentifier";

    private const string OrderScopeColumn = "guest_order_identifier = @GuestOrderIdentifier";

    private static string OrdersTemplate(string scope) => $"""
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS PersonIdentifier,
               owner.username                       AS Username,
               owner.display_name                   AS DisplayName,
               guest_order.created_at               AS CreatedAt
        FROM guest_order
        INNER JOIN person AS owner
                ON owner.person_identifier = guest_order.person_identifier
        WHERE guest_order.{scope}
        ORDER BY guest_order.created_at, guest_order.guest_order_identifier;
        """;

    // The actor-name expression is the one DapperCounterBoardReads and DapperMenuEventLog already use;
    // both joins are INNER because both foreign keys are NOT NULL.
    private static string EventsTemplate(string scope) => $"""
        SELECT order_event.order_event_identifier  AS OrderEventIdentifier,
               order_event.guest_order_identifier  AS GuestOrderIdentifier,
               order_event.sequence_number         AS SequenceNumber,
               order_event.event_type              AS EventType,
               order_event.actor_person_identifier AS ActorPersonIdentifier,
               COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)
                                                   AS ActorName,
               order_event.actor_role              AS ActorRole,
               order_event.occurred_at             AS OccurredAt
        FROM order_event
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = order_event.guest_order_identifier
        INNER JOIN person AS actor
                ON actor.person_identifier = order_event.actor_person_identifier
        WHERE guest_order.{scope}
        ORDER BY order_event.guest_order_identifier, order_event.sequence_number;
        """;

    /// <summary>
    /// The five typed operation tables folded into one flat result by <c>UNION ALL</c>, the shape
    /// <c>OrderEventReader</c> established. Every branch projects the same eleven columns with the
    /// missing ones cast to their target type (<c>NULL::numeric(10,2)</c>, <c>NULL::text</c>), because in
    /// a union PostgreSQL resolves each column's type from the branches and a bare <c>NULL</c> would
    /// leave it <c>unknown</c>.
    ///
    /// <para>Each of the four non-adding branches joins back to <c>order_operation_line_added</c> on
    /// <c>order_line_identifier</c> to recover the item and quantity. That join is exact and total, not a
    /// guess: the column is <c>NOT NULL UNIQUE</c> on the adding table ("the line's identity") and is
    /// the declared FK target of all four others, so exactly one origin row exists for every operation
    /// the database will accept. INNER JOIN rather than LEFT for the same reason — a LEFT would only
    /// invite nullable members for a row that cannot exist.</para>
    ///
    /// <para>Ordered by the operation's own primary key so a re-read is deterministic. That is not a
    /// promise about write order: the keys are UUIDv7 (ADR-0011) and two minted in the same millisecond
    /// differ only in their random bits. The schema records no ordinal within an event and nothing needs
    /// one — §6.5.5 forbids the one intra-event ordering that could change an outcome.</para>
    /// </summary>
    private static string OperationsTemplate(string scope) => $"""
        SELECT added.order_operation_line_added_identifier  AS OperationIdentifier,
               added.order_event_identifier                 AS OrderEventIdentifier,
               '{OrderEventVocabulary.LineAddedKind}'::text AS OperationKind,
               added.order_line_identifier                  AS OrderLineIdentifier,
               added.menu_item_identifier                   AS MenuItemIdentifier,
               added_item.name                              AS MenuItemName,
               added.quantity                               AS Quantity,
               added.unit_price_amount                      AS UnitPriceAmount,
               added.customization_note                     AS CustomizationNote,
               NULL::numeric(10,2)                          AS NewUnitPriceAmount,
               NULL::text                                   AS Reason
        FROM order_operation_line_added AS added
        INNER JOIN order_event AS added_event
                ON added_event.order_event_identifier = added.order_event_identifier
        INNER JOIN guest_order AS added_order
                ON added_order.guest_order_identifier = added_event.guest_order_identifier
        INNER JOIN menu_item AS added_item
                ON added_item.menu_item_identifier = added.menu_item_identifier
        WHERE added_order.{scope}

        UNION ALL

        SELECT removed.order_operation_line_removed_identifier,
               removed.order_event_identifier,
               '{OrderEventVocabulary.LineRemovedKind}'::text,
               removed.order_line_identifier,
               removed_origin.menu_item_identifier,
               removed_item.name,
               removed_origin.quantity,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               removed.reason
        FROM order_operation_line_removed AS removed
        INNER JOIN order_event AS removed_event
                ON removed_event.order_event_identifier = removed.order_event_identifier
        INNER JOIN guest_order AS removed_order
                ON removed_order.guest_order_identifier = removed_event.guest_order_identifier
        INNER JOIN order_operation_line_added AS removed_origin
                ON removed_origin.order_line_identifier = removed.order_line_identifier
        INNER JOIN menu_item AS removed_item
                ON removed_item.menu_item_identifier = removed_origin.menu_item_identifier
        WHERE removed_order.{scope}

        UNION ALL

        SELECT adjusted.order_operation_line_price_adjusted_identifier,
               adjusted.order_event_identifier,
               '{OrderEventVocabulary.LinePriceAdjustedKind}'::text,
               adjusted.order_line_identifier,
               adjusted_origin.menu_item_identifier,
               adjusted_item.name,
               adjusted_origin.quantity,
               NULL::numeric(10,2),
               NULL::text,
               adjusted.new_unit_price_amount,
               adjusted.reason
        FROM order_operation_line_price_adjusted AS adjusted
        INNER JOIN order_event AS adjusted_event
                ON adjusted_event.order_event_identifier = adjusted.order_event_identifier
        INNER JOIN guest_order AS adjusted_order
                ON adjusted_order.guest_order_identifier = adjusted_event.guest_order_identifier
        INNER JOIN order_operation_line_added AS adjusted_origin
                ON adjusted_origin.order_line_identifier = adjusted.order_line_identifier
        INNER JOIN menu_item AS adjusted_item
                ON adjusted_item.menu_item_identifier = adjusted_origin.menu_item_identifier
        WHERE adjusted_order.{scope}

        UNION ALL

        SELECT fulfilled.order_operation_line_fulfilled_identifier,
               fulfilled.order_event_identifier,
               '{OrderEventVocabulary.LineFulfilledKind}'::text,
               fulfilled.order_line_identifier,
               fulfilled_origin.menu_item_identifier,
               fulfilled_item.name,
               fulfilled_origin.quantity,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               NULL::text
        FROM order_operation_line_fulfilled AS fulfilled
        INNER JOIN order_event AS fulfilled_event
                ON fulfilled_event.order_event_identifier = fulfilled.order_event_identifier
        INNER JOIN guest_order AS fulfilled_order
                ON fulfilled_order.guest_order_identifier = fulfilled_event.guest_order_identifier
        INNER JOIN order_operation_line_added AS fulfilled_origin
                ON fulfilled_origin.order_line_identifier = fulfilled.order_line_identifier
        INNER JOIN menu_item AS fulfilled_item
                ON fulfilled_item.menu_item_identifier = fulfilled_origin.menu_item_identifier
        WHERE fulfilled_order.{scope}

        UNION ALL

        SELECT reverted.order_operation_line_fulfillment_reverted_identifier,
               reverted.order_event_identifier,
               '{OrderEventVocabulary.LineFulfillmentRevertedKind}'::text,
               reverted.order_line_identifier,
               reverted_origin.menu_item_identifier,
               reverted_item.name,
               reverted_origin.quantity,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               NULL::text
        FROM order_operation_line_fulfillment_reverted AS reverted
        INNER JOIN order_event AS reverted_event
                ON reverted_event.order_event_identifier = reverted.order_event_identifier
        INNER JOIN guest_order AS reverted_order
                ON reverted_order.guest_order_identifier = reverted_event.guest_order_identifier
        INNER JOIN order_operation_line_added AS reverted_origin
                ON reverted_origin.order_line_identifier = reverted.order_line_identifier
        INNER JOIN menu_item AS reverted_item
                ON reverted_item.menu_item_identifier = reverted_origin.menu_item_identifier
        WHERE reverted_order.{scope}

        ORDER BY OperationIdentifier;
        """;

    // Composed once at type initialisation, in textual order; both scope fragments are `const`, so there
    // is no initialisation-order hazard to reason about.
    private static readonly string OrdersBySittingSql = OrdersTemplate(SittingScopeColumn);

    private static readonly string EventsBySittingSql = EventsTemplate(SittingScopeColumn);

    private static readonly string OperationsBySittingSql = OperationsTemplate(SittingScopeColumn);

    private static readonly string OrdersByOrderSql = OrdersTemplate(OrderScopeColumn);

    private static readonly string EventsByOrderSql = EventsTemplate(OrderScopeColumn);

    private static readonly string OperationsByOrderSql = OperationsTemplate(OrderScopeColumn);

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperSittingRecordReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<SittingOrderRecord>> ListOrderRecordsForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default)
        => await ReadRecordsAsync(
            OrdersBySittingSql,
            EventsBySittingSql,
            OperationsBySittingSql,
            new { SittingIdentifier = sittingIdentifier },
            cancellationToken).ConfigureAwait(false);

    public async Task<SittingOrderRecord?> GetOrderRecordAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SittingOrderRecord> records = await ReadRecordsAsync(
            OrdersByOrderSql,
            EventsByOrderSql,
            OperationsByOrderSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            cancellationToken).ConfigureAwait(false);

        // guest_order_identifier is the primary key, so the orders query returned at most one row.
        return records.Count == 0 ? null : records[0];
    }

    /// <summary>
    /// The three queries and the in-memory grouping, once. The only thing that varies between the two
    /// public questions is which WHERE fragment the statements carry and which parameter satisfies it.
    /// </summary>
    private async Task<IReadOnlyList<SittingOrderRecord>> ReadRecordsAsync(
        string ordersSql,
        string eventsSql,
        string operationsSql,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OrderRow> orderRows = await connection
            .QueryAsync<OrderRow>(new CommandDefinition(
                ordersSql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        List<OrderRow> orders = orderRows.ToList();
        if (orders.Count == 0)
        {
            // No orders means no events and no operations. Two round trips saved on the very common case
            // of a table that has only just sat down.
            return [];
        }

        IEnumerable<EventRow> eventRows = await connection
            .QueryAsync<EventRow>(new CommandDefinition(
                eventsSql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        IEnumerable<OperationRow> operationRows = await connection
            .QueryAsync<OperationRow>(new CommandDefinition(
                operationsSql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        Dictionary<Guid, List<StoredOrderOperation>> operationsByEvent = [];
        foreach (OperationRow row in operationRows)
        {
            if (!operationsByEvent.TryGetValue(row.OrderEventIdentifier, out List<StoredOrderOperation>? operations))
            {
                operations = [];
                operationsByEvent[row.OrderEventIdentifier] = operations;
            }

            operations.Add(new StoredOrderOperation(
                row.OperationIdentifier,
                row.OrderEventIdentifier,
                row.OperationKind,
                row.OrderLineIdentifier,
                row.MenuItemIdentifier,
                row.MenuItemName,
                row.Quantity,
                row.UnitPriceAmount,
                row.CustomizationNote,
                row.NewUnitPriceAmount,
                row.Reason));
        }

        // The events query is already ordered by (order, sequence), so appending in read order keeps
        // each order's log ascending without a second sort.
        Dictionary<Guid, List<StoredOrderEvent>> eventsByOrder = [];
        foreach (EventRow row in eventRows)
        {
            if (!eventsByOrder.TryGetValue(row.GuestOrderIdentifier, out List<StoredOrderEvent>? events))
            {
                events = [];
                eventsByOrder[row.GuestOrderIdentifier] = events;
            }

            events.Add(new StoredOrderEvent(
                row.OrderEventIdentifier,
                row.GuestOrderIdentifier,
                row.SequenceNumber,
                row.EventType,
                row.ActorPersonIdentifier,
                row.ActorName,
                row.ActorRole,
                AsUtc(row.OccurredAt),
                operationsByEvent.TryGetValue(row.OrderEventIdentifier, out List<StoredOrderOperation>? found)
                    ? found
                    : []));
        }

        SittingOrderRecord[] records = new SittingOrderRecord[orders.Count];
        for (int index = 0; index < orders.Count; index++)
        {
            OrderRow order = orders[index];

            records[index] = new SittingOrderRecord(
                order.GuestOrderIdentifier,
                order.SittingIdentifier,
                order.PersonIdentifier,
                order.Username,
                order.DisplayName,
                AsUtc(order.CreatedAt),
                eventsByOrder.TryGetValue(order.GuestOrderIdentifier, out List<StoredOrderEvent>? events)
                    ? events
                    : []);
        }

        return records;
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Dapper binds these positional records by constructor-parameter name against the aliased columns
    // above; every member's CLR type matches exactly what Npgsql returns for that PostgreSQL type
    // (bigint → long, integer → int, numeric → decimal, timestamptz → DateTime), because Dapper's
    // constructor binding does not convert.
    private sealed record OrderRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid PersonIdentifier,
        string Username,
        string? DisplayName,
        DateTime CreatedAt);

    private sealed record EventRow(
        Guid OrderEventIdentifier,
        Guid GuestOrderIdentifier,
        long SequenceNumber,
        string EventType,
        Guid ActorPersonIdentifier,
        string ActorName,
        string ActorRole,
        DateTime OccurredAt);

    private sealed record OperationRow(
        Guid OperationIdentifier,
        Guid OrderEventIdentifier,
        string OperationKind,
        Guid OrderLineIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        int Quantity,
        decimal? UnitPriceAmount,
        string? CustomizationNote,
        decimal? NewUnitPriceAmount,
        string? Reason);
}
