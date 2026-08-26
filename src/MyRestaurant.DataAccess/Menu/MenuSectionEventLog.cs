using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuSectionEventEntry(
    Guid MenuSectionEventIdentifier,
    Guid MenuSectionIdentifier,
    string MenuSectionName,
    string EventType,
    string? NewName,
    string? NewDescription,
    int? NewDisplayOrder,
    Guid ActorPersonIdentifier,
    string ActorName,
    DateTimeOffset OccurredAt);

public interface IMenuSectionEventLog
{
    Task<IReadOnlyList<MenuSectionEventEntry>> ListForSectionAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuSectionEventLog : IMenuSectionEventLog
{
    private const string EventColumns = """
        menu_section_event.menu_section_event_identifier AS MenuSectionEventIdentifier,
        menu_section_event.menu_section_identifier       AS MenuSectionIdentifier,
        menu_section.name                                AS MenuSectionName,
        menu_section_event.event_type                    AS EventType,
        menu_section_event.new_name                      AS NewName,
        menu_section_event.new_description               AS NewDescription,
        menu_section_event.new_display_order             AS NewDisplayOrder,
        menu_section_event.actor_person_identifier       AS ActorPersonIdentifier,
        COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)
                                                         AS ActorName,
        menu_section_event.occurred_at                   AS OccurredAt
        """;

    private const string EventFrom = """
        FROM menu_section_event
        INNER JOIN menu_section
                ON menu_section.menu_section_identifier = menu_section_event.menu_section_identifier
        INNER JOIN person AS actor
                ON actor.person_identifier = menu_section_event.actor_person_identifier
        """;

    private static readonly string ForSectionSql = $"""
        SELECT {EventColumns}
        {EventFrom}
        WHERE menu_section_event.menu_section_identifier = @MenuSectionIdentifier
        ORDER BY menu_section_event.occurred_at, menu_section_event.menu_section_event_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuSectionEventLog(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuSectionEventEntry>> ListForSectionAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuSectionEventRow> rows = await connection
            .QueryAsync<MenuSectionEventRow>(new CommandDefinition(
                ForSectionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToEntry).ToArray();
    }

    private static MenuSectionEventEntry ToEntry(MenuSectionEventRow row) => new(
        row.MenuSectionEventIdentifier,
        row.MenuSectionIdentifier,
        row.MenuSectionName,
        row.EventType,
        row.NewName,
        row.NewDescription,
        row.NewDisplayOrder,
        row.ActorPersonIdentifier,
        row.ActorName,
        new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)));

    private sealed record MenuSectionEventRow(
        Guid MenuSectionEventIdentifier,
        Guid MenuSectionIdentifier,
        string MenuSectionName,
        string EventType,
        string? NewName,
        string? NewDescription,
        int? NewDisplayOrder,
        Guid ActorPersonIdentifier,
        string ActorName,
        DateTime OccurredAt);
}
