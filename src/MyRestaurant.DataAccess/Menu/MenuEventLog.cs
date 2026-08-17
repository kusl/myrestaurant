using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// One row of the append-only menu log, exactly as stored (TECHNICAL_SPECIFICATION §7, §11.4).
///
/// <para><see cref="EventType"/> is the stored string rather than an enum. §11.4 requires
/// administration to render "the complete stored record … never projected or truncated", and an enum is
/// a projection with a failure mode: a type this build does not know about would either throw or be
/// silently mapped to something wrong, and the one reader whose job is to show what is actually in the
/// table is the last place that should happen. A surface renders a friendly label for the types §8.2's
/// vocabulary CHECK admits and falls back to the raw string for anything else — so a future type shows up
/// as itself rather than as a lie or a crash. <b>That fallback has now been load-bearing twice</b>, which
/// is why no count of the vocabulary is written here: <c>0004</c> added two types and <c>0005</c> adds a
/// third, and a number in this comment would be one fact recorded where nothing can check it (F-77).</para>
///
/// <para>The typed nullable payload columns are each non-null for exactly the event types §8.2's named
/// paired CHECKs allow them on: the name for <c>created</c> and <c>name_changed</c>, the price for
/// <c>created</c> and <c>price_changed</c>, the description for <c>description_changed</c> alone, the
/// position for <c>reordered</c> alone, the section for <c>section_changed</c> alone, and nothing at all
/// for <c>activated</c> and <c>deactivated</c>. No count of them is written here, on the same reasoning
/// as the vocabulary above: <c>0005</c> has just made one true and left the sentence saying four.</para>
///
/// <para><b><c>created</c> carries neither a description nor a section although the item was created with
/// both.</b> §8.2 keeps that event at the name and the price, so an item created under a heading has a
/// <c>section_changed</c> beside its <c>created</c> — and a <c>description_changed</c> beside that when it
/// has a description — at the same instant, ordered after it by the identifier tiebreak both reads below
/// apply. That tiebreak holds because
/// <see cref="MyRestaurant.Domain.Identifiers.IIdentifierFactory"/> guarantees its output ascends, and
/// <em>not</em> because the values are UUIDv7: the format is ordered between milliseconds and random
/// inside one, so until F-95 this sentence was describing an outcome that occurred one time in six. That
/// ordering is not decoration: it is what makes the history read
/// <em>"Created as “Soup” at 4.50 / Filed under Starters / Description set"</em> rather than in whatever
/// sequence the scan returned.</para>
/// </summary>
/// <param name="MenuItemEventIdentifier">The event's UUIDv7 primary key (ADR-0011).</param>
/// <param name="MenuItemIdentifier">The item the event is about.</param>
/// <param name="MenuItemName">The item's name <em>now</em> — a read-time join, so a renamed item reads under its current name while <see cref="NewName"/> still says what each rename set it to.</param>
/// <param name="EventType">The stored <c>menu_item_event.event_type</c>.</param>
/// <param name="NewName">The name this event set, or <c>null</c> when the type does not carry one.</param>
/// <param name="NewPriceAmount">The price this event set, or <c>null</c> when the type does not carry one.</param>
/// <param name="NewDescription">The description this event set, or <c>null</c> when the type does not carry one. <c>""</c> is a value: it is what clearing a description stores.</param>
/// <param name="NewDisplayOrder">The position this event set, or <c>null</c> when the type does not carry one.</param>
/// <param name="NewMenuSectionIdentifier">The heading this event filed the item under, or <c>null</c> when the type does not carry one (<c>0005</c>).</param>
/// <param name="NewMenuSectionName">That heading's name <em>now</em>, joined at read time — <c>null</c> when the event carries no section. A renamed section reads under its current name, exactly as <see cref="MenuItemName"/> does for the item.</param>
/// <param name="ActorPersonIdentifier">Who did it.</param>
/// <param name="ActorName">Their display name, falling back to their username — the same rendering rule the counter board uses.</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
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

/// <summary>
/// Reads the menu's append-only event log (TECHNICAL_SPECIFICATION §7, §11.4: "Menu (CRUD + activity,
/// event history per item)"). The write side is <see cref="IMenuAdministration"/> for create, rename,
/// reprice, describe and reorder, and <see cref="IMenuAvailability"/> for the 86 toggle; this reads what
/// both of them wrote,
/// which is why it is neither's business and lives on its own.
///
/// <para><b>Nothing here is filtered or capped by default.</b> §11.4 is explicit that administration
/// renders the complete stored record and that "filters narrow only on explicit request". So the
/// per-item history returns every event that item has ever had, oldest first, with no page size: it is
/// the answer to "why does this cost what it costs", and a truncated answer to that question is worse
/// than no answer. The one capped read is the cross-item activity feed, whose cap is the explicit
/// request — it exists to fill a panel on the menu index, not to be the archive.</para>
/// </summary>
public interface IMenuEventLog
{
    /// <summary>
    /// Every event for one item, oldest first — the per-item history of §11.4. A history reads forward,
    /// so <c>created</c> is at the top and the most recent change is at the bottom, which is also the
    /// order in which a price argument is settled. An unknown identifier yields an empty list rather than
    /// an error: the item may have existed and the link may be stale, and the page above says so.
    /// </summary>
    Task<IReadOnlyList<MenuItemEventEntry>> ListForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent events across every item, newest first, capped at
    /// <paramref name="maximumCount"/> — the "activity" half of §11.4's menu section, which is what tells
    /// an administrator opening the page that somebody 86'd two things an hour ago. A non-positive cap
    /// returns nothing without a round trip.
    /// </summary>
    Task<IReadOnlyList<MenuItemEventEntry>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuEventLog"/>. One connection per call from the singleton
/// <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns aliased to
/// the record's member names, every column reference table-qualified — <c>menu_item</c> and
/// <c>menu_item_event</c> both carry a <c>menu_item_identifier</c>, and an unqualified reference to it is
/// exactly how PostgreSQL error 42702 bites — and rows read into an internal row type with a
/// <see cref="DateTime"/> member before being projected, because Npgsql materialises <c>timestamptz</c>
/// as <see cref="DateTime"/> and Dapper's constructor binding will not feed one into a
/// <see cref="DateTimeOffset"/> parameter (the same fix every other reader in this layer carries).
/// </summary>
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

    /// <summary>
    /// The first two joins are INNER: <c>menu_item_identifier</c> and <c>actor_person_identifier</c> are
    /// NOT NULL foreign keys, so a LEFT JOIN would only invite a nullable member for a row that cannot
    /// exist. The third is LEFT for the mirror-image reason —
    /// <c>menu_item_event.new_menu_section_identifier</c> is a <em>payload</em> column and is NULL on
    /// every event type but <c>section_changed</c>, so an INNER join there would silently drop every
    /// other event in the log.
    ///
    /// <para>The alias is <c>new_section</c> rather than <c>menu_section</c>, and it is load-bearing:
    /// <c>menu_item</c> gained its own <c>menu_section_identifier</c> in <c>0005</c>, so an unaliased
    /// join would read as a join to the item's <em>current</em> heading — which is a different fact from
    /// the one this event recorded, and the difference is invisible until somebody moves an item.</para>
    /// </summary>
    private const string EventFrom = """
        FROM menu_item_event
        INNER JOIN menu_item
                ON menu_item.menu_item_identifier = menu_item_event.menu_item_identifier
        INNER JOIN person AS actor
                ON actor.person_identifier = menu_item_event.actor_person_identifier
        LEFT JOIN menu_section AS new_section
               ON new_section.menu_section_identifier = menu_item_event.new_menu_section_identifier
        """;

    // Built at type-init (static readonly, not const) so the shared fragments interpolate once.
    //
    // The identifier is the tiebreak for two events that share an instant — which they do whenever one
    // transaction writes the row and its events together. That works because IIdentifierFactory
    // guarantees successive identifiers ascend under this exact ORDER BY, and it is worth naming the
    // guarantee rather than the format: what stood here said "UUIDv7 keys are time-ordered", which is
    // true between milliseconds and false inside one, and Guid.CreateVersion7()
    // leaves the sub-millisecond bits random. Every history in §11.4 read its same-instant events in
    // whatever order the random bits fell (F-95). Nothing changed here; the factory changed.
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
        // A non-positive cap would make LIMIT 0 (or an error); asking for nothing is answered without a
        // round trip rather than by an exception a caller has to defend against.
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
