using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// One row of the append-only menu item <em>picture</em> log, exactly as stored
/// (TECHNICAL_SPECIFICATION §7, §8.2, §11.4).
///
/// <para><b>Why this is a third reader rather than a widened <see cref="IMenuEventLog"/>.</b> The same
/// argument <see cref="MenuSectionEventEntry"/> carries, and one more that is stronger than either of
/// theirs. Three tables, three vocabularies, three subjects: <c>menu_item_event</c> and
/// <c>menu_item_image_event</c> both hang off <c>menu_item</c> and share not one type word between them,
/// and neither log's payload columns exist on the other. The extra argument is
/// <see cref="MenuItemImageIdentifier"/> — an event on this table names a row that is <em>frequently
/// gone</em>, because §7's replace mints a new identifier and deletes the old one and its removal deletes
/// the row outright. A merged reader would have to carry a nullable column meaning "the subject of this
/// event still exists" for one of its three streams and never for the other two.</para>
///
/// <para><b>Nothing is joined to <c>menu_item_image</c>, and that is the load-bearing shape of this
/// read.</b> <c>0006</c> put no foreign key from the log to the picture precisely because the picture is
/// deleted while its history is kept, so an INNER JOIN here would return only the events about whichever
/// picture happens to be attached <em>now</em> — a history that silently begins at the current photograph
/// — and a LEFT JOIN would add a column that is null for every event but the newest. The identifier is
/// carried bare, on <c>0006</c>'s own reading of what it is for: not a pointer to a row a reader can open,
/// but the evidence that the URL changed.</para>
///
/// <para><see cref="EventType"/> is the stored string rather than an enum, for
/// <see cref="MenuItemEventEntry.EventType"/>'s reason: §11.4 requires administration to render "the
/// complete stored record … never projected or truncated", and an enum is a projection whose failure mode
/// is to throw or to silently mis-map a type this build has not met. A surface renders a friendly label
/// for the types §8.2's CHECK admits and falls back to the raw string for anything else. <b>No count of
/// the vocabulary is written here</b>, on F-77's ruling — the <c>switch</c> arms on the surface are the
/// only census that cannot go stale.</para>
///
/// <para>The typed nullable payload columns are each non-null for exactly the types §8.2's three named
/// paired CHECKs allow them on, and the arrangement is the one most easily got backwards by somebody
/// holding all three of these files open: the format and the size for <c>attached</c> and
/// <c>replaced</c>, the caption for <c>alt_text_changed</c> <em>alone</em>, and nothing at all for
/// <c>removed</c> — the one type whose whole payload is its own name. That is why <c>0007</c> widened the
/// vocabulary and neither existing biconditional: a caption is not a fact about the file.</para>
/// </summary>
/// <param name="MenuItemImageEventIdentifier">The event's UUIDv7 primary key (ADR-0011).</param>
/// <param name="MenuItemIdentifier">The item the event is about. The log hangs off the item rather than off the picture, because the picture is gone by design.</param>
/// <param name="MenuItemName">The item's name <em>now</em> — a read-time join, so a renamed dish reads under its current name. The same rule <see cref="MenuSectionEventEntry.MenuSectionName"/> follows.</param>
/// <param name="MenuItemImageIdentifier">Which picture this event was about. A bare identifier with no row behind it after a replace or a removal, which is the whole reason <c>0006</c> declared no foreign key for it.</param>
/// <param name="EventType">The stored <c>menu_item_image_event.event_type</c>.</param>
/// <param name="NewContentType">The media type this event's picture was, or <c>null</c> when the type does not carry one.</param>
/// <param name="NewByteLength">How large it was, or <c>null</c> when the type does not carry one. <b>It is a column on the log and not on the picture</b>, because after a removal the bytes are gone and this is the only place the number can live (F-101).</param>
/// <param name="NewAltText">The caption this event set, or <c>null</c> when the type does not carry one. <c>""</c> is a value: it is what clearing a caption stores.</param>
/// <param name="ActorPersonIdentifier">Who did it.</param>
/// <param name="ActorName">Their display name, falling back to their username — the same rendering rule every other reader in this layer uses.</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
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

/// <summary>
/// Reads the menu items' append-only picture log (TECHNICAL_SPECIFICATION §7, §11.4). The write side is
/// <see cref="IMenuItemImageAdministration"/>; this reads what it wrote, which is why it is neither that
/// interface's business nor <see cref="IMenuEventLog"/>'s and lives on its own — exactly as
/// <see cref="IMenuSectionEventLog"/> does beside them.
///
/// <para><b>It arrives with the surface that renders it, and that is why it did not arrive with
/// <c>0006</c>.</b> The schema, both write verbs and their integration facts landed three slices before
/// this file, and §16.4 recorded the absence of this interface by name for each of them: a read with no
/// caller is the same defect this project keeps recording about workflow verbs, weaker only because an
/// unread read cannot change anything without telling anybody. The facts that needed the history read the
/// table directly in the meantime, which is stated in <c>MenuItemImageTests</c> rather than hidden, and
/// that arrangement is what this interface replaces.</para>
///
/// <para><b>Nothing here is filtered or capped.</b> §11.4 is explicit that administration renders the
/// complete stored record and that "filters narrow only on explicit request". So the per-item history
/// returns every picture event that dish has ever had, oldest first, with no page size: it is the answer
/// to "when did this photograph last change, and who changed it", and a truncated answer to that
/// question is worse than no answer. A dish that has had four photographs has four <c>attached</c> or
/// <c>replaced</c> rows and the identifiers of three pictures nobody can fetch any more — which is the
/// history rather than a defect in it.</para>
///
/// <para>There is deliberately <b>no</b> cross-item picture feed to match
/// <see cref="IMenuEventLog.ListRecentAsync"/>, on that method's own reasoning: that one exists to fill a
/// panel on <c>/administration/menu</c>, and there is no such panel for pictures. Inventing a read with no
/// caller in the slice whose subject is a read that finally has one would be a poor joke.</para>
/// </summary>
public interface IMenuItemImageEventLog
{
    /// <summary>
    /// Every picture event for one item, oldest first — the per-item picture history of §11.4. A history
    /// reads forward, so the first photograph is at the top and the most recent change is at the bottom.
    /// An unknown identifier yields an empty list rather than an error, and so does an item that has
    /// never had a picture: both are ordinary, and the page above says so in a sentence.
    /// </summary>
    Task<IReadOnlyList<MenuItemImageEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuItemImageEventLog"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (this is a lone read), columns
/// aliased to the record's member names, every column reference table-qualified — <c>menu_item</c> and
/// <c>menu_item_image_event</c> both carry a <c>menu_item_identifier</c>, and an unqualified reference to
/// it is exactly how PostgreSQL error 42702 bites — and rows read into an internal row type with a
/// <see cref="DateTime"/> member before being projected, because Npgsql materialises <c>timestamptz</c> as
/// <see cref="DateTime"/> and Dapper's constructor binding will not feed one into a
/// <see cref="DateTimeOffset"/> parameter (the same fix every other reader in this layer carries).
///
/// <para>Both joins are INNER and there is no third one: <c>menu_item_identifier</c> and
/// <c>actor_person_identifier</c> are NOT NULL foreign keys, so a LEFT JOIN would invite a nullable member
/// for a row that cannot exist. <b><c>menu_item_image_identifier</c> is the one this read must not
/// join</b>, and it is the one a reader would reach for first — see
/// <see cref="MenuItemImageEventEntry"/>.</para>
///
/// <para>The ORDER BY is <c>(occurred_at, menu_item_image_event_identifier)</c>, which is the index
/// <c>0006</c> declared read in the order it declared it: UUIDv7 keys are time-ordered, so the identifier
/// is a stable tiebreak for two events sharing an instant. They <em>do</em> share one — a replace writes
/// its row and its event in one transaction off one <c>_clock.UtcNow</c> — so this is the difference
/// between a deterministic history and one that reorders itself between two reads.</para>
/// </summary>
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

    // Built at type-init (static readonly, not const) so the shared fragments interpolate once.
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
