using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Orders;

public sealed record PersonOrderHistoryEntry(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid TableIdentifier,
    string TableLabel,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    decimal PersonTotalAmount,
    IReadOnlyList<OrderLineView> Lines)
{
    public int LineCount => Lines.Count;
}

public sealed record HiddenOrderFilter(
    string? Username = null,
    DateTimeOffset? OpenedFrom = null,
    DateTimeOffset? OpenedBefore = null,
    Guid? TableIdentifier = null)
{
    public static HiddenOrderFilter Everything { get; } = new();

    public bool IsNarrowed
        => !string.IsNullOrWhiteSpace(Username)
           || OpenedFrom is not null
           || OpenedBefore is not null
           || TableIdentifier is not null;
}

public sealed record HiddenOrderSummary(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid TableIdentifier,
    string TableLabel,
    Guid OwnerPersonIdentifier,
    string Username,
    string? DisplayName,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal? SettledTotalAmount,
    decimal PersonTotalAmount,
    int LineCount,
    DateTimeOffset HiddenAt,
    Guid HiddenByPersonIdentifier,
    string HiddenByName)
{
    public string OwnerName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

public sealed record OrderVisibilityEntry(
    Guid VisibilityEventIdentifier,
    Guid GuestOrderIdentifier,
    string EventType,
    Guid ActorPersonIdentifier,
    string ActorName,
    DateTimeOffset OccurredAt);

public interface IOrderHistoryReads
{
    Task<IReadOnlyList<PersonOrderHistoryEntry>> ListVisibleHistoryForPersonAsync(
        Guid personIdentifier,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HiddenOrderSummary>> ListHiddenOrdersAsync(
        HiddenOrderFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderVisibilityEntry>> ListVisibilityLogAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperOrderHistoryReads : IOrderHistoryReads
{
    private const char LikeEscape = '\\';

    private const string PersonHistorySql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               sitting.restaurant_table_identifier  AS TableIdentifier,
               restaurant_table.label               AS TableLabel,
               sitting.opened_at                    AS OpenedAt,
               sitting.closed_at                    AS ClosedAt,
               COALESCE(bill.person_total_amount, 0)::numeric(10,2)
                                                    AS PersonTotalAmount
        FROM guest_order
        INNER JOIN table_sitting AS sitting
                ON sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = sitting.restaurant_table_identifier
        LEFT JOIN sitting_bill AS bill
                ON bill.guest_order_identifier = guest_order.guest_order_identifier
        LEFT JOIN order_visibility_current AS visibility
                ON visibility.guest_order_identifier = guest_order.guest_order_identifier
        WHERE guest_order.person_identifier = @PersonIdentifier
          AND sitting.closed_at IS NOT NULL
          AND NOT COALESCE(visibility.is_hidden, false)
        ORDER BY sitting.closed_at DESC, restaurant_table.label, guest_order.guest_order_identifier
        LIMIT @MaximumCount;
        """;

    private const string PersonHistoryLinesSql = """
        SELECT line.guest_order_identifier          AS GuestOrderIdentifier,
               line.order_line_identifier           AS OrderLineIdentifier,
               line.menu_item_identifier            AS MenuItemIdentifier,
               line.menu_item_name                  AS MenuItemName,
               line.quantity                        AS Quantity,
               line.current_unit_price_amount       AS CurrentUnitPriceAmount,
               line.customization_note              AS CustomizationNote,
               line.is_fulfilled                    AS IsFulfilled,
               line.added_at                        AS AddedAt,
               line.added_by_order_event_identifier AS AddedByOrderEventIdentifier
        FROM order_current_line AS line
        WHERE line.guest_order_identifier = ANY(@GuestOrderIdentifiers)
        ORDER BY line.guest_order_identifier, line.added_at, line.order_line_identifier;
        """;

    private static readonly string HiddenOrdersSql = $"""
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               sitting.restaurant_table_identifier  AS TableIdentifier,
               restaurant_table.label               AS TableLabel,
               guest_order.person_identifier        AS OwnerPersonIdentifier,
               owner.username                       AS Username,
               owner.display_name                   AS DisplayName,
               sitting.opened_at                    AS OpenedAt,
               sitting.closed_at                    AS ClosedAt,
               sitting.settled_total_amount         AS SettledTotalAmount,
               COALESCE(bill.person_total_amount, 0)::numeric(10,2)
                                                    AS PersonTotalAmount,
               COALESCE(line_summary.line_count, 0) AS LineCount,
               latest.occurred_at                   AS HiddenAt,
               latest.actor_person_identifier        AS HiddenByPersonIdentifier,
               COALESCE(NULLIF(btrim(hider.display_name), ''), hider.username)
                                                    AS HiddenByName
        FROM guest_order
        CROSS JOIN LATERAL (
            SELECT visibility.order_visibility_event_identifier,
                   visibility.event_type,
                   visibility.actor_person_identifier,
                   visibility.occurred_at
            FROM order_visibility_event AS visibility
            WHERE visibility.guest_order_identifier = guest_order.guest_order_identifier
            ORDER BY visibility.occurred_at DESC,
                     visibility.order_visibility_event_identifier DESC
            LIMIT 1
        ) AS latest
        INNER JOIN table_sitting AS sitting
                ON sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        INNER JOIN restaurant_table
                ON restaurant_table.restaurant_table_identifier = sitting.restaurant_table_identifier
        INNER JOIN person AS owner
                ON owner.person_identifier = guest_order.person_identifier
        INNER JOIN person AS hider
                ON hider.person_identifier = latest.actor_person_identifier
        LEFT JOIN sitting_bill AS bill
                ON bill.guest_order_identifier = guest_order.guest_order_identifier
        LEFT JOIN LATERAL (
            SELECT count(*)::int AS line_count
            FROM order_current_line AS line
            WHERE line.guest_order_identifier = guest_order.guest_order_identifier
        ) AS line_summary ON true
        WHERE latest.event_type = '{OrderEventVocabulary.HiddenVisibility}'
          AND (@UsernamePattern IS NULL
               OR owner.username::text ILIKE @UsernamePattern ESCAPE '{LikeEscape}')
          AND (@OpenedFrom IS NULL OR sitting.opened_at >= @OpenedFrom)
          AND (@OpenedBefore IS NULL OR sitting.opened_at < @OpenedBefore)
          AND (@TableIdentifier IS NULL OR sitting.restaurant_table_identifier = @TableIdentifier)
        ORDER BY latest.occurred_at DESC, guest_order.guest_order_identifier DESC
        LIMIT @MaximumCount;
        """;

    private const string VisibilityLogSql = """
        SELECT visibility.order_visibility_event_identifier AS VisibilityEventIdentifier,
               visibility.guest_order_identifier            AS GuestOrderIdentifier,
               visibility.event_type                        AS EventType,
               visibility.actor_person_identifier           AS ActorPersonIdentifier,
               COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)
                                                            AS ActorName,
               visibility.occurred_at                       AS OccurredAt
        FROM order_visibility_event AS visibility
        INNER JOIN person AS actor
                ON actor.person_identifier = visibility.actor_person_identifier
        WHERE visibility.guest_order_identifier = @GuestOrderIdentifier
        ORDER BY visibility.occurred_at, visibility.order_visibility_event_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperOrderHistoryReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<PersonOrderHistoryEntry>> ListVisibleHistoryForPersonAsync(
        Guid personIdentifier,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<HistoryRow> historyRows = await connection
            .QueryAsync<HistoryRow>(new CommandDefinition(
                PersonHistorySql,
                new { PersonIdentifier = personIdentifier, MaximumCount = maximumCount },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        List<HistoryRow> orders = historyRows.ToList();
        if (orders.Count == 0)
        {
            return [];
        }

        Guid[] orderIdentifiers = orders.Select(order => order.GuestOrderIdentifier).ToArray();

        IEnumerable<LineRow> lineRows = await connection
            .QueryAsync<LineRow>(new CommandDefinition(
                PersonHistoryLinesSql,
                new { GuestOrderIdentifiers = orderIdentifiers },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        Dictionary<Guid, List<OrderLineView>> linesByOrder = [];
        foreach (LineRow row in lineRows)
        {
            if (!linesByOrder.TryGetValue(row.GuestOrderIdentifier, out List<OrderLineView>? lines))
            {
                lines = [];
                linesByOrder[row.GuestOrderIdentifier] = lines;
            }

            lines.Add(new OrderLineView(
                row.GuestOrderIdentifier,
                row.OrderLineIdentifier,
                row.MenuItemIdentifier,
                row.MenuItemName,
                row.Quantity,
                row.CurrentUnitPriceAmount,
                row.CustomizationNote,
                row.IsFulfilled,
                AsUtc(row.AddedAt),
                row.AddedByOrderEventIdentifier));
        }

        PersonOrderHistoryEntry[] entries = new PersonOrderHistoryEntry[orders.Count];
        for (int index = 0; index < orders.Count; index++)
        {
            HistoryRow order = orders[index];

            entries[index] = new PersonOrderHistoryEntry(
                order.GuestOrderIdentifier,
                order.SittingIdentifier,
                order.TableIdentifier,
                order.TableLabel,
                AsUtc(order.OpenedAt),
                AsUtc(order.ClosedAt),
                order.PersonTotalAmount,
                linesByOrder.TryGetValue(order.GuestOrderIdentifier, out List<OrderLineView>? found)
                    ? found
                    : []);
        }

        return entries;
    }

    public async Task<IReadOnlyList<HiddenOrderSummary>> ListHiddenOrdersAsync(
        HiddenOrderFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (maximumCount <= 0)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<HiddenRow> rows = await connection
            .QueryAsync<HiddenRow>(new CommandDefinition(
                HiddenOrdersSql,
                new
                {
                    UsernamePattern = SubstringPattern(filter.Username),
                    filter.OpenedFrom,
                    filter.OpenedBefore,
                    filter.TableIdentifier,
                    MaximumCount = maximumCount,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(row => new HiddenOrderSummary(
            row.GuestOrderIdentifier,
            row.SittingIdentifier,
            row.TableIdentifier,
            row.TableLabel,
            row.OwnerPersonIdentifier,
            row.Username,
            row.DisplayName,
            AsUtc(row.OpenedAt),
            row.ClosedAt is { } closedAt ? AsUtc(closedAt) : null,
            row.SettledTotalAmount,
            row.PersonTotalAmount,
            row.LineCount,
            AsUtc(row.HiddenAt),
            row.HiddenByPersonIdentifier,
            row.HiddenByName)).ToArray();
    }

    public async Task<IReadOnlyList<OrderVisibilityEntry>> ListVisibilityLogAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<VisibilityRow> rows = await connection
            .QueryAsync<VisibilityRow>(new CommandDefinition(
                VisibilityLogSql,
                new { GuestOrderIdentifier = guestOrderIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(row => new OrderVisibilityEntry(
            row.VisibilityEventIdentifier,
            row.GuestOrderIdentifier,
            row.EventType,
            row.ActorPersonIdentifier,
            row.ActorName,
            AsUtc(row.OccurredAt))).ToArray();
    }

    private static string? SubstringPattern(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        string escaped = term
            .Trim()
            .Replace(LikeEscape.ToString(), $"{LikeEscape}{LikeEscape}", StringComparison.Ordinal)
            .Replace("%", $"{LikeEscape}%", StringComparison.Ordinal)
            .Replace("_", $"{LikeEscape}_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record HistoryRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid TableIdentifier,
        string TableLabel,
        DateTime OpenedAt,
        DateTime ClosedAt,
        decimal PersonTotalAmount);

    private sealed record LineRow(
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

    private sealed record HiddenRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid TableIdentifier,
        string TableLabel,
        Guid OwnerPersonIdentifier,
        string Username,
        string? DisplayName,
        DateTime OpenedAt,
        DateTime? ClosedAt,
        decimal? SettledTotalAmount,
        decimal PersonTotalAmount,
        int LineCount,
        DateTime HiddenAt,
        Guid HiddenByPersonIdentifier,
        string HiddenByName);

    private sealed record VisibilityRow(
        Guid VisibilityEventIdentifier,
        Guid GuestOrderIdentifier,
        string EventType,
        Guid ActorPersonIdentifier,
        string ActorName,
        DateTime OccurredAt);
}
