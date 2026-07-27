using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// One line the kitchen has recently marked fulfilled, in an open sitting
/// (TECHNICAL_SPECIFICATION §11.2: "an Undo affordance on recently-fulfilled lines →
/// <c>fulfillment_reversal</c>").
///
/// <para>It carries the same grouping keys as <see cref="KitchenPendingLineView"/> so the board can put
/// an undone line straight back into the ticket it came from, plus the one field the pending view has no
/// reason to hold: <see cref="FulfilledAt"/>, the instant of the <em>latest</em> fulfillment flip. That
/// instant is what "recently" means, and it is deliberately not the same as the line's
/// <see cref="AddedAt"/> — a line sent an hour ago and fulfilled ten seconds ago is the one the kitchen
/// wants to undo.</para>
/// </summary>
public sealed record KitchenFulfilledLineView(
    Guid GuestOrderIdentifier,
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote,
    DateTimeOffset AddedAt,
    DateTimeOffset FulfilledAt,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    string PersonName,
    Guid TableIdentifier,
    string TableLabel);

/// <summary>
/// The kitchen board's second read (TECHNICAL_SPECIFICATION §11.2). The first — the pending queue — is
/// <see cref="IOrderReadModel.ListKitchenPendingLinesAsync"/> over the <c>kitchen_pending_line</c> view,
/// and it stays there because that view is one of the four §8.3 projections.
///
/// <para>This one is not a §8.3 view and deliberately does not become one. "Recently fulfilled" is a
/// question about <em>when a flip happened</em>, which the projection views answer with a boolean and
/// nothing else: <c>order_current_line.is_fulfilled</c> is the latest flip's direction with its instant
/// thrown away. Adding a timestamp column to a schema-of-record view to serve one Undo button would
/// change the schema §8.3 pins; asking the operation tables directly does not, and the question is
/// honestly a different one. Hence a separate interface rather than a sixth method on the read
/// model.</para>
/// </summary>
public interface IKitchenBoardReads
{
    /// <summary>
    /// Every currently-fulfilled, non-removed line in an open sitting whose fulfillment landed at or
    /// after <paramref name="fulfilledSince"/>, most recently fulfilled first — the Undo list (§11.2).
    ///
    /// <para>A line whose fulfillment was already reversed is absent (its latest flip is a reversal, so
    /// it is pending again and belongs in the queue instead), and so is a removed line, because
    /// <c>order_current_line</c> excludes it.</para>
    /// </summary>
    Task<IReadOnlyList<KitchenFulfilledLineView>> ListRecentlyFulfilledLinesAsync(
        DateTimeOffset fulfilledSince,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IKitchenBoardReads"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction, columns aliased to the record's
/// member names, and rows read into an internal row type with <see cref="DateTime"/> members before
/// being projected — Npgsql materialises <c>timestamptz</c> as <see cref="DateTime"/> and Dapper's
/// constructor binding will not feed one into a <see cref="DateTimeOffset"/> parameter (the same fix
/// <c>DapperOrderReadModel</c>, <c>DapperMenuDirectory</c>, and <c>DapperTableDirectory</c> carry).
/// </summary>
public sealed class DapperKitchenBoardReads : IKitchenBoardReads
{
    /// <summary>
    /// The lateral picks the highest-sequence fulfillment for the line. Because the outer filter also
    /// demands <c>line.is_fulfilled</c> — which the view computes as "the latest flip of either kind was
    /// a fulfillment" — that row is necessarily the flip currently in force, so its instant is the
    /// moment the line became fulfilled. A line fulfilled, reverted, and fulfilled again reports the
    /// second fulfillment, which is the right answer for an Undo button.
    /// </summary>
    private const string RecentlyFulfilledSql = """
        SELECT line.guest_order_identifier                  AS GuestOrderIdentifier,
               line.order_line_identifier                   AS OrderLineIdentifier,
               line.menu_item_identifier                    AS MenuItemIdentifier,
               line.menu_item_name                          AS MenuItemName,
               line.quantity                                AS Quantity,
               line.customization_note                      AS CustomizationNote,
               line.added_at                                AS AddedAt,
               latest_fulfillment.occurred_at               AS FulfilledAt,
               guest_order.table_sitting_identifier         AS SittingIdentifier,
               guest_order.person_identifier                AS PersonIdentifier,
               COALESCE(NULLIF(btrim(person.display_name), ''), person.username)
                                                            AS PersonName,
               table_sitting.restaurant_table_identifier    AS TableIdentifier,
               restaurant_table.label                       AS TableLabel
        FROM order_current_line AS line
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = line.guest_order_identifier
        INNER JOIN person
                ON person.person_identifier = guest_order.person_identifier
        INNER JOIN table_sitting
                ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = table_sitting.restaurant_table_identifier
        INNER JOIN LATERAL (
            SELECT fulfilled_event.occurred_at
            FROM order_operation_line_fulfilled AS fulfilled
            INNER JOIN order_event AS fulfilled_event
                    ON fulfilled_event.order_event_identifier = fulfilled.order_event_identifier
            WHERE fulfilled.order_line_identifier = line.order_line_identifier
            ORDER BY fulfilled_event.sequence_number DESC
            LIMIT 1
        ) AS latest_fulfillment ON true
        WHERE table_sitting.closed_at IS NULL
          AND line.is_fulfilled
          AND latest_fulfillment.occurred_at >= @FulfilledSince
        ORDER BY latest_fulfillment.occurred_at DESC, line.order_line_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperKitchenBoardReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<KitchenFulfilledLineView>> ListRecentlyFulfilledLinesAsync(
        DateTimeOffset fulfilledSince,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<FulfilledLineRow> rows = await connection
            .QueryAsync<FulfilledLineRow>(new CommandDefinition(
                RecentlyFulfilledSql,
                new { FulfilledSince = fulfilledSince },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToView).ToArray();
    }

    private static KitchenFulfilledLineView ToView(FulfilledLineRow row) => new(
        row.GuestOrderIdentifier,
        row.OrderLineIdentifier,
        row.MenuItemIdentifier,
        row.MenuItemName,
        row.Quantity,
        row.CustomizationNote,
        AsUtc(row.AddedAt),
        AsUtc(row.FulfilledAt),
        row.SittingIdentifier,
        row.PersonIdentifier,
        row.PersonName,
        row.TableIdentifier,
        row.TableLabel);

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record FulfilledLineRow(
        Guid GuestOrderIdentifier,
        Guid OrderLineIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        int Quantity,
        string? CustomizationNote,
        DateTime AddedAt,
        DateTime FulfilledAt,
        Guid SittingIdentifier,
        Guid PersonIdentifier,
        string PersonName,
        Guid TableIdentifier,
        string TableLabel);
}
