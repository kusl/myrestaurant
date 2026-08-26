using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Orders;

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

public interface IKitchenBoardReads
{
    Task<IReadOnlyList<KitchenFulfilledLineView>> ListRecentlyFulfilledLinesAsync(
        DateTimeOffset fulfilledSince,
        CancellationToken cancellationToken = default);
}

public sealed class DapperKitchenBoardReads : IKitchenBoardReads
{
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
