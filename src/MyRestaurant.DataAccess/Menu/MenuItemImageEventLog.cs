using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemImageEventEntry(
    Guid MenuItemImageEventIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    Guid MenuItemImageIdentifier,
    string EventType,
    string? NewContentType,
    int? NewByteLength,
    string? NewAltText,
    Guid ActorPersonIdentifier,
    string ActorName,
    DateTimeOffset OccurredAt);

public interface IMenuItemImageEventLog
{
    Task<IReadOnlyList<MenuItemImageEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuItemImageEventLog : IMenuItemImageEventLog
{
    private const string EventColumns = """
        menu_item_image_event.menu_item_image_event_identifier AS MenuItemImageEventIdentifier,
        menu_item_image_event.menu_item_identifier             AS MenuItemIdentifier,
        menu_item.name                                         AS MenuItemName,
        menu_item_image_event.menu_item_image_identifier       AS MenuItemImageIdentifier,
        menu_item_image_event.event_type                       AS EventType,
        menu_item_image_event.new_content_type                 AS NewContentType,
        menu_item_image_event.new_byte_length                  AS NewByteLength,
        menu_item_image_event.new_alt_text                     AS NewAltText,
        menu_item_image_event.actor_person_identifier          AS ActorPersonIdentifier,
        COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)
                                                               AS ActorName,
        menu_item_image_event.occurred_at                      AS OccurredAt
        """;

    private const string EventFrom = """
        FROM menu_item_image_event
        INNER JOIN menu_item
                ON menu_item.menu_item_identifier = menu_item_image_event.menu_item_identifier
        INNER JOIN person AS actor
                ON actor.person_identifier = menu_item_image_event.actor_person_identifier
        """;

    private static readonly string ForItemSql = $"""
        SELECT {EventColumns}
        {EventFrom}
        WHERE menu_item_image_event.menu_item_identifier = @MenuItemIdentifier
        ORDER BY menu_item_image_event.occurred_at,
                 menu_item_image_event.menu_item_image_event_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuItemImageEventLog(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemImageEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemImageEventRow> rows = await connection
            .QueryAsync<MenuItemImageEventRow>(new CommandDefinition(
                ForItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToEntry).ToArray();
    }

    private static MenuItemImageEventEntry ToEntry(MenuItemImageEventRow row) => new(
        row.MenuItemImageEventIdentifier,
        row.MenuItemIdentifier,
        row.MenuItemName,
        row.MenuItemImageIdentifier,
        row.EventType,
        row.NewContentType,
        row.NewByteLength,
        row.NewAltText,
        row.ActorPersonIdentifier,
        row.ActorName,
        new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)));

    private sealed record MenuItemImageEventRow(
        Guid MenuItemImageEventIdentifier,
        Guid MenuItemIdentifier,
        string MenuItemName,
        Guid MenuItemImageIdentifier,
        string EventType,
        string? NewContentType,
        int? NewByteLength,
        string? NewAltText,
        Guid ActorPersonIdentifier,
        string ActorName,
        DateTime OccurredAt);
}
