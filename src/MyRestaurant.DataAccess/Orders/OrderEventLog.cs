using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Orders;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// Reads one order's complete append-only event log as domain
/// <see cref="OrderEvent"/>s (TECHNICAL_SPECIFICATION §6.2, §6.3, §8.5).
///
/// <para>This is the source of truth being read, not a projection: the same list feeds
/// <see cref="OrderProjection.FromEvents"/> (the fold whose equivalence with the SQL views §8.5 asserts),
/// the §6.5 validation the order-mutating transaction runs under the lock (§6.6), and the administration
/// event explorer's "complete stored record, never projected or truncated" requirement (§11.4). Nothing
/// is filtered out and nothing is summarised — removed lines, reverted fulfillments, and superseded
/// prices are all still here, because the whole point of an event log is that the history survives the
/// state.</para>
/// </summary>
public interface IOrderEventLog
{
    /// <summary>
    /// Every event on the order, ascending by <c>sequence_number</c>, each carrying its typed operations
    /// in the order they were written. An unknown order yields an empty list rather than throwing — a
    /// living order is created lazily (§6.1), so "no events yet" and "no order yet" are the same answer
    /// to a reader.
    /// </summary>
    Task<IReadOnlyList<OrderEvent>> ReadEventsAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IOrderEventLog"/>. One connection per call, no transaction —
/// the transactional read used inside the order-mutating transaction goes through
/// <see cref="OrderEventReader"/> directly, sharing exactly the same SQL so the validator can never
/// disagree with a reader about what the log says.
/// </summary>
public sealed class DapperOrderEventLog : IOrderEventLog
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperOrderEventLog(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<OrderEvent>> ReadEventsAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await OrderEventReader
            .ReadAsync(connection, transaction: null, guestOrderIdentifier, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The shared two-query read behind <see cref="IOrderEventLog"/> and the order-mutating transaction.
///
/// <para>Two queries, not six: the event headers, and then the five typed operation tables folded into
/// one flat result by <c>UNION ALL</c>. Every branch projects the same nine columns with the missing ones
/// cast to their target type (<c>NULL::uuid</c>, <c>NULL::numeric(10,2)</c>, …), because in a union
/// PostgreSQL resolves the column type from the branches and a bare <c>NULL</c> would leave it
/// <c>unknown</c>. Operations are ordered by their surrogate primary key so a re-read is deterministic,
/// but that order is <em>not</em> a promise about the order they were written in: the keys are UUIDv7
/// (ADR-0011) and two minted inside the same millisecond differ only in their random bits. The schema
/// records no ordinal within an event and nothing needs one — §6.5.5 forbids the one intra-event
/// ordering that could change an outcome (removing a line the same event added), and the SQL views break
/// same-event ties arbitrarily too, since every operation of one event shares its sequence number.</para>
/// </summary>
internal static class OrderEventReader
{
    private const string EventsSql = """
        SELECT order_event.order_event_identifier  AS OrderEventIdentifier,
               order_event.guest_order_identifier  AS GuestOrderIdentifier,
               order_event.sequence_number         AS SequenceNumber,
               order_event.event_type              AS EventType,
               order_event.actor_person_identifier AS ActorPersonIdentifier,
               order_event.actor_role              AS ActorRole,
               order_event.occurred_at             AS OccurredAt
        FROM order_event
        WHERE order_event.guest_order_identifier = @GuestOrderIdentifier
        ORDER BY order_event.sequence_number;
        """;

    private static readonly string OperationsSql = $"""
        SELECT added.order_operation_line_added_identifier AS OperationIdentifier,
               added.order_event_identifier                AS OrderEventIdentifier,
               '{OrderEventVocabulary.LineAddedKind}'::text AS OperationKind,
               added.order_line_identifier                 AS OrderLineIdentifier,
               added.menu_item_identifier                  AS MenuItemIdentifier,
               added.quantity                              AS Quantity,
               added.unit_price_amount                     AS UnitPriceAmount,
               added.customization_note                    AS CustomizationNote,
               NULL::numeric(10,2)                         AS NewUnitPriceAmount,
               NULL::text                                  AS Reason
        FROM order_operation_line_added AS added
        INNER JOIN order_event AS added_event
                ON added_event.order_event_identifier = added.order_event_identifier
        WHERE added_event.guest_order_identifier = @GuestOrderIdentifier

        UNION ALL

        SELECT removed.order_operation_line_removed_identifier,
               removed.order_event_identifier,
               '{OrderEventVocabulary.LineRemovedKind}'::text,
               removed.order_line_identifier,
               NULL::uuid,
               NULL::integer,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               removed.reason
        FROM order_operation_line_removed AS removed
        INNER JOIN order_event AS removed_event
                ON removed_event.order_event_identifier = removed.order_event_identifier
        WHERE removed_event.guest_order_identifier = @GuestOrderIdentifier

        UNION ALL

        SELECT adjusted.order_operation_line_price_adjusted_identifier,
               adjusted.order_event_identifier,
               '{OrderEventVocabulary.LinePriceAdjustedKind}'::text,
               adjusted.order_line_identifier,
               NULL::uuid,
               NULL::integer,
               NULL::numeric(10,2),
               NULL::text,
               adjusted.new_unit_price_amount,
               adjusted.reason
        FROM order_operation_line_price_adjusted AS adjusted
        INNER JOIN order_event AS adjusted_event
                ON adjusted_event.order_event_identifier = adjusted.order_event_identifier
        WHERE adjusted_event.guest_order_identifier = @GuestOrderIdentifier

        UNION ALL

        SELECT fulfilled.order_operation_line_fulfilled_identifier,
               fulfilled.order_event_identifier,
               '{OrderEventVocabulary.LineFulfilledKind}'::text,
               fulfilled.order_line_identifier,
               NULL::uuid,
               NULL::integer,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               NULL::text
        FROM order_operation_line_fulfilled AS fulfilled
        INNER JOIN order_event AS fulfilled_event
                ON fulfilled_event.order_event_identifier = fulfilled.order_event_identifier
        WHERE fulfilled_event.guest_order_identifier = @GuestOrderIdentifier

        UNION ALL

        SELECT reverted.order_operation_line_fulfillment_reverted_identifier,
               reverted.order_event_identifier,
               '{OrderEventVocabulary.LineFulfillmentRevertedKind}'::text,
               reverted.order_line_identifier,
               NULL::uuid,
               NULL::integer,
               NULL::numeric(10,2),
               NULL::text,
               NULL::numeric(10,2),
               NULL::text
        FROM order_operation_line_fulfillment_reverted AS reverted
        INNER JOIN order_event AS reverted_event
                ON reverted_event.order_event_identifier = reverted.order_event_identifier
        WHERE reverted_event.guest_order_identifier = @GuestOrderIdentifier

        ORDER BY OperationIdentifier;
        """;

    public static async Task<IReadOnlyList<OrderEvent>> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken)
    {
        object parameters = new { GuestOrderIdentifier = guestOrderIdentifier };

        IEnumerable<OrderEventRow> eventRows = await connection.QueryAsync<OrderEventRow>(new CommandDefinition(
            EventsSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        List<OrderEventRow> headers = eventRows.ToList();
        if (headers.Count == 0)
        {
            return [];
        }

        IEnumerable<OrderOperationRow> operationRows = await connection.QueryAsync<OrderOperationRow>(new CommandDefinition(
            OperationsSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Dictionary<Guid, List<OrderOperation>> operationsByEvent = [];
        foreach (OrderOperationRow row in operationRows)
        {
            if (!operationsByEvent.TryGetValue(row.OrderEventIdentifier, out List<OrderOperation>? operations))
            {
                operations = [];
                operationsByEvent[row.OrderEventIdentifier] = operations;
            }

            operations.Add(ToOperation(row));
        }

        OrderEvent[] events = new OrderEvent[headers.Count];
        for (int index = 0; index < headers.Count; index++)
        {
            OrderEventRow header = headers[index];
            IReadOnlyList<OrderOperation> operations =
                operationsByEvent.TryGetValue(header.OrderEventIdentifier, out List<OrderOperation>? found)
                    ? found
                    : [];

            events[index] = new OrderEvent(
                header.OrderEventIdentifier,
                header.GuestOrderIdentifier,
                header.SequenceNumber,
                OrderEventVocabulary.EventTypeFrom(header.EventType),
                header.ActorPersonIdentifier,
                OrderEventVocabulary.ActorRoleFrom(header.ActorRole),
                new DateTimeOffset(DateTime.SpecifyKind(header.OccurredAt, DateTimeKind.Utc)),
                operations);
        }

        return events;
    }

    private static OrderOperation ToOperation(OrderOperationRow row) => row.OperationKind switch
    {
        OrderEventVocabulary.LineAddedKind => new LineAddedOperation(
            row.OrderLineIdentifier,
            Required(row.MenuItemIdentifier, nameof(row.MenuItemIdentifier)),
            Required(row.Quantity, nameof(row.Quantity)),
            Required(row.UnitPriceAmount, nameof(row.UnitPriceAmount)),
            row.CustomizationNote),
        OrderEventVocabulary.LineRemovedKind => new LineRemovedOperation(row.OrderLineIdentifier, row.Reason),
        OrderEventVocabulary.LinePriceAdjustedKind => new LinePriceAdjustedOperation(
            row.OrderLineIdentifier,
            Required(row.NewUnitPriceAmount, nameof(row.NewUnitPriceAmount)),
            Required(row.Reason, nameof(row.Reason))),
        OrderEventVocabulary.LineFulfilledKind => new LineFulfilledOperation(row.OrderLineIdentifier),
        OrderEventVocabulary.LineFulfillmentRevertedKind => new LineFulfillmentRevertedOperation(row.OrderLineIdentifier),
        _ => throw new InvalidOperationException($"Unknown stored order operation kind '{row.OperationKind}'."),
    };

    // The columns below are NOT NULL in their own table and only become nullable in the union; a null
    // here means a branch of the UNION ALL projects the wrong column, which is a bug in this file.
    private static T Required<T>(T? value, string columnName)
        where T : struct
        => value ?? throw new InvalidOperationException($"Stored order operation is missing '{columnName}'.");

    private static string Required(string? value, string columnName)
        => value ?? throw new InvalidOperationException($"Stored order operation is missing '{columnName}'.");

    // Dapper binds these positional records by constructor-parameter name against the aliased columns
    // above; every member's CLR type matches exactly what Npgsql returns for that PostgreSQL type
    // (bigint → long, integer → int, numeric → decimal, timestamptz → DateTime), because Dapper's
    // constructor binding does not convert.
    private sealed record OrderEventRow(
        Guid OrderEventIdentifier,
        Guid GuestOrderIdentifier,
        long SequenceNumber,
        string EventType,
        Guid ActorPersonIdentifier,
        string ActorRole,
        DateTime OccurredAt);

    private sealed record OrderOperationRow(
        Guid OperationIdentifier,
        Guid OrderEventIdentifier,
        string OperationKind,
        Guid OrderLineIdentifier,
        Guid? MenuItemIdentifier,
        int? Quantity,
        decimal? UnitPriceAmount,
        string? CustomizationNote,
        decimal? NewUnitPriceAmount,
        string? Reason);
}
