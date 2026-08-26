using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Orders;

namespace MyRestaurant.DataAccess.Sittings;

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

public sealed record SittingOrderRecord(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StoredOrderEvent> Events)
{
    public string OwnerName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

public interface ISittingRecordReads
{
    Task<IReadOnlyList<SittingOrderRecord>> ListOrderRecordsForSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken = default);

    Task<SittingOrderRecord?> GetOrderRecordAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperSittingRecordReads : ISittingRecordReads
{
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

        return records.Count == 0 ? null : records[0];
    }

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
