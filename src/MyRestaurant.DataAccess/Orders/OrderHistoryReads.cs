using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// One of a person's own past orders (TECHNICAL_SPECIFICATION §11.1: "history — the guest's <b>own</b>
/// past orders at this restaurant"; §6.8).
///
/// <para><b>Past</b> means the sitting is closed. A living order is not history: the surface built for it
/// is <c>/table/{id}</c>, which shows it with its running total, its pending badges, and the Send button
/// that adds to it. A sitting that has been settled has none of those, and its total will never move
/// again except by an administrator's §6.7 correction.</para>
///
/// <para><b>Cross-member history is never shown</b> (§11.1), so there is no party roster here and no
/// other member's total — only what this person ordered, at which table, on which evening. The reader
/// enforces that by taking the person's identifier rather than the sitting's.</para>
///
/// <para><see cref="Lines"/> is the <em>projection</em>, not the record: removed lines are absent and a
/// repriced line shows its current price. That is the right answer for the person who ate the meal, who
/// is asking what they had and what it cost. The complete stored log — the removals, the reversals, the
/// superseded prices — is administration's (§11.4), and this is deliberately not that screen.</para>
/// </summary>
/// <param name="GuestOrderIdentifier">The order's UUIDv7 primary key (ADR-0011).</param>
/// <param name="SittingIdentifier">The settled sitting it belongs to.</param>
/// <param name="TableIdentifier">The table that sitting was on (§4.1).</param>
/// <param name="TableLabel">That table's human label.</param>
/// <param name="OpenedAt">When the sitting opened (§5.1) — the evening the person remembers.</param>
/// <param name="ClosedAt">When it was closed and settled (§5.3). Never null: this reader only returns closed sittings.</param>
/// <param name="PersonTotalAmount">This person's own share of the bill, from <c>sitting_bill</c> (§8.3).</param>
/// <param name="Lines">Their current lines, oldest first — the projection, not the log.</param>
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
    /// <summary>How many current lines the order has — what "3 items" on the summary row counts.</summary>
    public int LineCount => Lines.Count;
}

/// <summary>
/// How an administrator narrows the hidden-records view (TECHNICAL_SPECIFICATION §6.8: "filterable by
/// username, date range, and table"; §11.4: "filters narrow only on explicit request").
///
/// <para>Every member is optional and <see cref="Everything"/> is the unfiltered default, because §11.4
/// is explicit that the administrator's screens start complete and narrow only when asked. A filter that
/// defaulted to "today" would quietly answer a different question from the one §6.8 poses, which is
/// "where is the record somebody hid".</para>
///
/// <para>The date range is on the sitting's <c>opened_at</c> rather than on when the order was hidden.
/// Both are defensible and only one can be the default; the reason to prefer the meal is that the person
/// asking has a date for the <em>meal</em> — "the Tuesday we had the big table" — and no idea at all when
/// somebody later tidied their history. When the sitting's date is unknown, the list is already ordered
/// most-recently-hidden first, which is the other question answered without a filter.</para>
/// </summary>
/// <param name="Username">A substring of the owner's username, or <c>null</c> for every owner. Matching is case-insensitive, and <c>%</c>, <c>_</c> and <c>\</c> in the text are matched literally rather than as wildcards.</param>
/// <param name="OpenedFrom">Only sittings that opened at or after this instant, or <c>null</c> for no lower bound.</param>
/// <param name="OpenedBefore">Only sittings that opened strictly before this instant, or <c>null</c> for no upper bound. Half-open on purpose: a caller turning a calendar day in the restaurant's zone into a range passes the start of that day and the start of the next, and never has to reason about whether 23:59:59.999999 is inside it.</param>
/// <param name="TableIdentifier">Only sittings on this table, or <c>null</c> for every table.</param>
public sealed record HiddenOrderFilter(
    string? Username = null,
    DateTimeOffset? OpenedFrom = null,
    DateTimeOffset? OpenedBefore = null,
    Guid? TableIdentifier = null)
{
    /// <summary>Every hidden order in the restaurant — the state the §11.4 view opens in.</summary>
    public static HiddenOrderFilter Everything { get; } = new();

    /// <summary>True when at least one bound is set, which is what the surface says out loud above the list.</summary>
    public bool IsNarrowed
        => !string.IsNullOrWhiteSpace(Username)
           || OpenedFrom is not null
           || OpenedBefore is not null
           || TableIdentifier is not null;
}

/// <summary>
/// One currently-hidden order as the §11.4 hidden-records view lists it: who owns it, which table and
/// which evening, what it came to, and who hid it when.
///
/// <para>Both totals are here for the reason §5.3 gives: <c>settled_total_amount</c> is the table's
/// stamped total and is never rewritten, while <see cref="PersonTotalAmount"/> is this one person's
/// current share of it. They answer different questions and neither substitutes for the other — a party
/// of six settling at 214.00 tells an administrator nothing about whose 31.50 was hidden.</para>
/// </summary>
/// <param name="GuestOrderIdentifier">The hidden order's UUIDv7 primary key (ADR-0011).</param>
/// <param name="SittingIdentifier">The sitting it belongs to.</param>
/// <param name="TableIdentifier">The table that sitting was on.</param>
/// <param name="TableLabel">That table's human label.</param>
/// <param name="OwnerPersonIdentifier">Whose order it is.</param>
/// <param name="Username">Their username — the thing §6.8's filter matches on.</param>
/// <param name="DisplayName">Their display name, when they have set one.</param>
/// <param name="OpenedAt">When the sitting opened (§5.1).</param>
/// <param name="ClosedAt">When it was settled, or <c>null</c> if it somehow is not — see <see cref="IOrderHistoryReads.ListHiddenOrdersAsync"/>.</param>
/// <param name="SettledTotalAmount">The table's stamped settled total, on the same terms (§5.3).</param>
/// <param name="PersonTotalAmount">This person's current share of the bill, from <c>sitting_bill</c> (§8.3).</param>
/// <param name="LineCount">How many current lines the order has.</param>
/// <param name="HiddenAt">When the <c>hidden</c> row that is currently in force was appended.</param>
/// <param name="HiddenByPersonIdentifier">Who appended it — the owner, in every case the system can produce.</param>
/// <param name="HiddenByName">That person's display name, falling back to their username.</param>
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
    /// <summary>The name to head the row with: the display name when set, otherwise the username.</summary>
    public string OwnerName => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

/// <summary>
/// One row of one order's visibility log (TECHNICAL_SPECIFICATION §6.8, §11.4: the expanded record shows
/// the "visibility log").
///
/// <para><see cref="EventType"/> is the stored string rather than an enum, the same decision
/// <see cref="Sittings.StoredOrderEvent"/> and <see cref="Menu.MenuItemEventEntry"/> made and for the
/// same reason: §11.4 requires administration to render what is stored, and the one reader whose job is
/// to show that is the last place a word should be mapped to something it is not.</para>
/// </summary>
/// <param name="VisibilityEventIdentifier">The row's UUIDv7 primary key (ADR-0011).</param>
/// <param name="GuestOrderIdentifier">The order it concerns.</param>
/// <param name="EventType">The stored <c>order_visibility_event.event_type</c> — <c>hidden</c> or <c>unhidden</c> (§8.2).</param>
/// <param name="ActorPersonIdentifier">Who did it: the owner for a hide, an administrator for an unhide (§6.8).</param>
/// <param name="ActorName">Their display name, falling back to their username.</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
public sealed record OrderVisibilityEntry(
    Guid VisibilityEventIdentifier,
    Guid GuestOrderIdentifier,
    string EventType,
    Guid ActorPersonIdentifier,
    string ActorName,
    DateTimeOffset OccurredAt);

/// <summary>
/// The read side of §6.8: what a person sees of their own past, what an administrator sees of what has
/// been hidden, and the visibility log behind both.
///
/// <para><b>Why a fourth reader of the order tables.</b> <see cref="IOrderReadModel"/> answers questions
/// scoped to an order or a sitting the caller already names; <see cref="IOrderEventLog"/> feeds the §8.5
/// fold and the validator; <see cref="Sittings.ISittingRecordReads"/> renders one sitting's complete
/// stored record. None of the three can answer "which of <em>this person's</em> orders, across every
/// sitting they have ever been in, may they still see" — and that question is the whole of §11.1's
/// history section. Widening any of them would put a visibility filter into a type whose other callers
/// must never have one: the kitchen, the counter, and administration "always see everything" (§6.8).</para>
///
/// <para><b>Hiding is enforced here, once.</b> Both person-scoped queries exclude hidden orders in SQL
/// rather than trusting a surface to filter them, because §6.8's guarantee is that a hidden order is gone
/// from the owner's own views — and a guarantee that depends on every future page remembering a
/// <c>Where</c> clause is not one. <see cref="ListHiddenOrdersAsync"/> is the deliberate inverse and is
/// reached only from a surface behind <c>area.administration</c> (§3.7).</para>
/// </summary>
public interface IOrderHistoryReads
{
    /// <summary>
    /// One person's own past orders that they have not hidden: settled sittings only, most recently
    /// settled first, capped at <paramref name="maximumCount"/>.
    ///
    /// <para>Capped because this is a phone screen and a regular's history is unbounded, and ordered
    /// newest-first because the order somebody wants is nearly always the last one. An order in an open
    /// sitting is absent by design — it is not history yet, and the live surface owns it.</para>
    /// </summary>
    Task<IReadOnlyList<PersonOrderHistoryEntry>> ListVisibleHistoryForPersonAsync(
        Guid personIdentifier,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// §6.8's hidden-records view: every order in the restaurant whose latest visibility event is a
    /// <c>hidden</c>, narrowed by <paramref name="filter"/>, most recently hidden first, capped at
    /// <paramref name="maximumCount"/>.
    ///
    /// <para>"Currently hidden" is the same definition <c>order_visibility_current</c> encodes — the
    /// latest event by <c>occurred_at</c>, tie-broken by identifier — so an order that was hidden,
    /// unhidden, and hidden again appears once, dated by the hide that is in force. An order that was
    /// unhidden does not appear at all, which is what makes the Unhide button's effect visible: the row
    /// leaves the list.</para>
    ///
    /// <para>The sitting is closed in every case the application can produce, because
    /// <see cref="IOrderVisibility.HideAsync"/> refuses an open one. It is not <em>filtered</em> to
    /// closed here, and that is on purpose: if a row for an open sitting ever appeared, this is the one
    /// screen that must show it rather than hide the anomaly (§11.4).</para>
    /// </summary>
    Task<IReadOnlyList<HiddenOrderSummary>> ListHiddenOrdersAsync(
        HiddenOrderFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One order's complete visibility log, oldest first — the part of §11.4's expanded record that says
    /// who hid this and whether it has been round the loop before. Uncapped, like every other log
    /// administration reads: an order has at most a handful of these, and the point of a log is that it
    /// is all there.
    /// </summary>
    Task<IReadOnlyList<OrderVisibilityEntry>> ListVisibilityLogAsync(
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IOrderHistoryReads"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns
/// aliased to the records' member names, every column reference table-qualified — <c>guest_order</c>,
/// <c>table_sitting</c>, <c>order_visibility_event</c> and the projection views all carry same-named
/// identifier columns, and an unqualified reference to one is exactly how PostgreSQL error 42702 bites —
/// and rows read into internal row types whose members match what Npgsql returns before being projected,
/// because <c>timestamptz</c> arrives as <see cref="DateTime"/> and Dapper's constructor binding will not
/// feed one into a <see cref="DateTimeOffset"/> parameter.
/// </summary>
public sealed class DapperOrderHistoryReads : IOrderHistoryReads
{
    /// <summary>
    /// The escape character for the username <c>LIKE</c> pattern. A backslash is the conventional choice
    /// and, with <c>standard_conforming_strings</c> on (the default since PostgreSQL 9.1), <c>'\'</c> in
    /// the SQL below is a single literal backslash rather than the start of an escape sequence.
    /// </summary>
    private const char LikeEscape = '\\';

    /// <summary>
    /// One person's settled, non-hidden orders. The <c>LEFT JOIN</c> to
    /// <c>order_visibility_current</c> with a <c>COALESCE</c> is what makes "never had a visibility event"
    /// and "explicitly unhidden" the same answer, which they are (§6.8: current flag = latest event, and
    /// no events means not hidden).
    ///
    /// <para><c>sitting_bill</c> is also joined <c>LEFT</c>, even though the view is built from
    /// <c>guest_order</c> and therefore has a row for every order: the aggregate is
    /// <c>COALESCE</c>d to zero anyway, and an <c>INNER</c> join here would silently drop an order from
    /// somebody's history if that view ever grew a restriction. History that quietly loses a meal is the
    /// failure this whole slice exists to make impossible.</para>
    /// </summary>
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

    /// <summary>
    /// The lines of the orders the query above returned, in one round trip rather than one per order.
    /// <c>= ANY(@GuestOrderIdentifiers)</c> binds the <c>Guid[]</c> as a single <c>uuid[]</c> parameter,
    /// the shape <c>DapperMenuDirectory</c> already uses. Scoped to the identifiers rather than
    /// re-deriving the person's orders, so the two results cannot disagree about which orders are in the
    /// answer.
    /// </summary>
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

    /// <summary>
    /// Every currently-hidden order, narrowed by the four optional bounds §6.8 names.
    ///
    /// <para><c>CROSS JOIN LATERAL … LIMIT 1</c> picks the latest visibility event per order and, because
    /// a lateral join drops rows whose subquery yields nothing, silently excludes every order that has
    /// never had one — which is almost all of them. Its <c>ORDER BY</c> reproduces
    /// <c>order_visibility_current</c>'s tie-break (<c>occurred_at DESC</c>, then identifier
    /// <c>DESC</c>) character for character: two readers of the same log that disagree about which event
    /// is latest would put an order in this list and in its owner's history at the same time.</para>
    ///
    /// <para>Each filter is written <c>@Parameter IS NULL OR …</c> so one statement serves every
    /// combination. The alternative — composing the WHERE clause in C# — is how a reader ends up with
    /// eight code paths and one of them untested.</para>
    ///
    /// <para>The username match is <c>username::text ILIKE …</c> rather than a bare <c>LIKE</c> on the
    /// column. <c>person.username</c> is <c>citext</c> (§8.2), which already compares case-insensitively
    /// under equality, but which of the <c>citext</c> extension's pattern operators a mixed
    /// <c>citext</c>/<c>text</c> comparison resolves to is not something this query should be quietly
    /// depending on. The cast plus <c>ILIKE</c> is a core-PostgreSQL operator whose behaviour does not
    /// move if the column's type ever does.</para>
    /// </summary>
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

    /// <summary>
    /// One order's visibility log. The actor-name expression is the one every other staff-facing reader
    /// uses; the join is INNER because <c>actor_person_identifier</c> is NOT NULL.
    /// </summary>
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
            // Nobody's first visit has a history. One round trip saved on the commonest case there is.
            return [];
        }

        Guid[] orderIdentifiers = orders.Select(order => order.GuestOrderIdentifier).ToArray();

        IEnumerable<LineRow> lineRows = await connection
            .QueryAsync<LineRow>(new CommandDefinition(
                PersonHistoryLinesSql,
                new { GuestOrderIdentifiers = orderIdentifiers },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // The lines query is already ordered by (order, added_at, line), so appending in read order keeps
        // each order's lines oldest-first without a second sort.
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

    /// <summary>
    /// Turns a search term into a <c>LIKE</c> pattern that matches it as a literal substring. The three
    /// characters <c>LIKE</c> gives meaning to — <c>\</c>, <c>%</c>, <c>_</c> — are escaped, so searching
    /// for <c>a_b</c> finds the username <c>a_b</c> and not <c>axb</c>. Whitespace-only input is no
    /// filter at all rather than a pattern matching everything, which is the same answer but says so.
    /// </summary>
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

    // Dapper binds these positional records by constructor-parameter name against the aliased columns
    // above; every member's CLR type matches exactly what Npgsql returns for that PostgreSQL type
    // (integer → int, numeric → decimal, timestamptz → DateTime, citext → string), because Dapper's
    // constructor binding does not convert.
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
