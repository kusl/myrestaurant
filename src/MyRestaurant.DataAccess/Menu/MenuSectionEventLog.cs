using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// One row of the append-only menu <em>section</em> log, exactly as stored (TECHNICAL_SPECIFICATION §7,
/// §11.4).
///
/// <para><b>Why this is a second reader rather than a widened <see cref="IMenuEventLog"/>.</b> Two
/// tables, two vocabularies and two subjects. <c>menu_item_event</c> and <c>menu_section_event</c> share
/// three of their type words and mean different things by all three — a <c>renamed</c> section is not a
/// <c>name_changed</c> item, and neither log's payload columns exist on the other. A <c>UNION ALL</c>
/// over both is a real read that §11.4's event explorer may want one day; it is not this, and building
/// one here would make the per-section history pay for a merge it never uses.</para>
///
/// <para><see cref="EventType"/> is the stored string rather than an enum, for the reason
/// <see cref="MenuItemEventEntry.EventType"/> is: §11.4 requires administration to render "the complete
/// stored record … never projected or truncated", and an enum is a projection whose failure mode is to
/// throw or to silently mis-map a type this build has not met. A surface renders a friendly label for the
/// types §8.2's CHECK admits and falls back to the raw string for anything else. <b>No count of the
/// vocabulary is written here</b>, on F-77's ruling — the <c>switch</c> arms on the surface are the only
/// census that cannot go stale.</para>
///
/// <para>The typed nullable payload columns are each non-null for exactly the types §8.2's three named
/// paired CHECKs allow them on: the name for <c>created</c> and <c>renamed</c>, the description for
/// <c>created</c> and <c>described</c>, the position for <c>created</c> and <c>reordered</c>, and nothing
/// at all for <c>activated</c> and <c>deactivated</c> — the two types whose whole payload is their own
/// name.</para>
///
/// <para><b><c>created</c> carries all three, which is the opposite of the item log's rule</b> and is
/// worth reading twice by anyone holding both files open. §8.2 keeps <c>menu_item_event.created</c> at the
/// name and the price, so an item created under a heading with a description writes three rows. A section
/// has three payload columns and its <c>created</c> carries every one of them, so creating a heading
/// writes exactly one row and its history opens with a line that says everything about the moment it was
/// made.</para>
/// </summary>
/// <param name="MenuSectionEventIdentifier">The event's UUIDv7 primary key (ADR-0011).</param>
/// <param name="MenuSectionIdentifier">The section the event is about.</param>
/// <param name="MenuSectionName">The section's name <em>now</em> — a read-time join, so a renamed heading reads under its current name while <see cref="NewName"/> still says what each rename set it to.</param>
/// <param name="EventType">The stored <c>menu_section_event.event_type</c>.</param>
/// <param name="NewName">The name this event set, or <c>null</c> when the type does not carry one.</param>
/// <param name="NewDescription">The description this event set, or <c>null</c> when the type does not carry one. <c>""</c> is a value: it is what clearing a description stores.</param>
/// <param name="NewDisplayOrder">The position this event set, or <c>null</c> when the type does not carry one.</param>
/// <param name="ActorPersonIdentifier">Who did it.</param>
/// <param name="ActorName">Their display name, falling back to their username — the same rendering rule every other reader in this layer uses.</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
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

/// <summary>
/// Reads the menu sections' append-only event log (TECHNICAL_SPECIFICATION §7, §11.4: "Menu (CRUD +
/// activity, event history per item)" — which §11.4's section editor now answers one register up, per
/// heading). The write side is <see cref="IMenuSectionAdministration"/>; this reads what it wrote, which
/// is why it is neither that interface's business nor <see cref="IMenuEventLog"/>'s and lives on its own.
///
/// <para><b>Nothing here is filtered or capped.</b> §11.4 is explicit that administration renders the
/// complete stored record and that "filters narrow only on explicit request". So the per-section history
/// returns every event that heading has ever had, oldest first, with no page size: it is the answer to
/// "why is this heading called what it is called, and who switched it off", and a truncated answer to that
/// question is worse than no answer.</para>
///
/// <para>There is deliberately <b>no</b> cross-section activity feed to match
/// <see cref="IMenuEventLog.ListRecentAsync"/>. That one exists to fill a panel on
/// <c>/administration/menu</c>, which is an index over items; sections have no such panel and inventing a
/// read with no caller would be the same mistake this project keeps recording about workflow verbs — a
/// code path no test can reach through the interface meant to protect it.</para>
/// </summary>
public interface IMenuSectionEventLog
{
    /// <summary>
    /// Every event for one section, oldest first — the per-section history of §11.4. A history reads
    /// forward, so <c>created</c> is at the top and the most recent change is at the bottom. An unknown
    /// identifier yields an empty list rather than an error: the section may have existed and the link may
    /// be stale, and the page above says so.
    /// </summary>
    Task<IReadOnlyList<MenuSectionEventEntry>> ListForSectionAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuSectionEventLog"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (this is a lone read), columns
/// aliased to the record's member names, every column reference table-qualified — <c>menu_section</c> and
/// <c>menu_section_event</c> both carry a <c>menu_section_identifier</c>, and an unqualified reference to
/// it is exactly how PostgreSQL error 42702 bites — and rows read into an internal row type with a
/// <see cref="DateTime"/> member before being projected, because Npgsql materialises <c>timestamptz</c> as
/// <see cref="DateTime"/> and Dapper's constructor binding will not feed one into a
/// <see cref="DateTimeOffset"/> parameter (the same fix every other reader in this layer carries).
///
/// <para>Both joins are INNER: <c>menu_section_identifier</c> and <c>actor_person_identifier</c> are NOT
/// NULL foreign keys, so a LEFT JOIN would invite a nullable member for a row that cannot exist. There is
/// no third join to alias, which is the one way this file is simpler than
/// <see cref="DapperMenuEventLog"/> — a section event's payload columns name no other row.</para>
/// </summary>
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

    // Built at type-init (static readonly, not const) so the shared fragments interpolate once.
    // UUIDv7 keys are time-ordered, so the identifier is a stable tiebreak for two events that share an
    // instant — which they do whenever one transaction writes a row and its event together.
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
