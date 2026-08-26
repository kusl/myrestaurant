using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemEventEntry(
    Guid MenuItemEventIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    string EventType,
    string? NewName,
    decimal? NewPriceAmount,
    string? NewDescription,
    int? NewDisplayOrder,
    Guid? NewMenuSectionIdentifier,
    string? NewMenuSectionName,
    Guid ActorPersonIdentifier,
    string ActorName,
    DateTimeOffset OccurredAt);

public interface IMenuEventLog
{
    Task<IReadOnlyList<MenuItemEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MenuItemEventEntry>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuEventLog : IMenuEventLog
{
    private const string EventColumns = """
        menu_item_event.menu_item_event_identifier AS MenuItemEventIdentifier,
        menu_item_event.menu_item_identifier       AS MenuItemIdentifier,
        menu_item.name                             AS MenuItemName,
        menu_item_event.event_type                 AS EventType,
        menu_item_event.new_name                   AS NewName,
        menu_item_event.new_price_amount           AS NewPriceAmount,
        menu_item_event.new_description             AS NewDescription,
        menu_item_event.new_display_order           AS NewDisplayOrder,
        menu_item_event.new_menu_section_identifier AS NewMenuSectionIdentifier,
        new_section.name                           AS NewMenuSectionName,
        menu_item_event.actor_person_identifier    AS ActorPersonIdentifier,
        COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)
                                                   AS ActorName,
        menu_item_event.occurred_at                AS OccurredAt
        """;

    private const string EventFrom = """
        FROM menu_item_event
        INNER JOIN menu_item
                ON menu_item.menu_item_identifier = menu_item_event.menu_item_identifier
        INNER JOIN person AS actor
                ON actor.person_identifier = menu_item_event.actor_person_identifier
        LEFT JOIN menu_section AS new_section
               ON new_section.menu_section_identifier = menu_item_event.new_menu_section_identifier
        """;

    private static readonly string ForItemSql = $"""
        SELECT {EventColumns}
        {EventFrom}
        WHERE menu_item_event.menu_item_identifier = @MenuItemIdentifier
        ORDER BY menu_item_event.occurred_at, menu_item_event.menu_item_event_identifier;
        """;

    private static readonly string RecentSql = $"""
        SELECT {EventColumns}
        {EventFrom}
        ORDER BY menu_item_event.occurred_at DESC, menu_item_event.menu_item_event_identifier DESC
        LIMIT @MaximumCount;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuEventLog(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemEventRow> rows = await connection
            .QueryAsync<MenuItemEventRow>(new CommandDefinition(
                ForItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToEntry).ToArray();
    }

    public async Task<IReadOnlyList<MenuItemEventEntry>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemEventRow> rows = await connection
            .QueryAsync<MenuItemEventRow>(new CommandDefinition(
                RecentSql,
                new { MaximumCount = maximumCount },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToEntry).ToArray();
    }

    private static MenuItemEventEntry ToEntry(MenuItemEventRow row) => new(
        row.MenuItemEventIdentifier,
        row.MenuItemIdentifier,
        row.MenuItemName,
        row.EventType,
        row.NewName,
        row.NewPriceAmount,
        row.NewDescription,
        row.NewDisplayOrder,
        row.NewMenuSectionIdentifier,
        row.NewMenuSectionName,
        row.ActorPersonIdentifier,
        row.ActorName,
        new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)));

    private sealed record MenuItemEventRow(
        Guid MenuItemEventIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        string EventType,
        string? NewName,
        decimal? NewPriceAmount,
        string? NewDescription,
        int? NewDisplayOrder,
        Guid? NewMenuSectionIdentifier,
        string? NewMenuSectionName,
        Guid ActorPersonIdentifier,
        string ActorName,
        DateTime OccurredAt);
}
