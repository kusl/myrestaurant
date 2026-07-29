using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.Authentication;

namespace MyRestaurant.DataAccess.Events;

/// <summary>
/// The three append-only streams the event explorer reads across (TECHNICAL_SPECIFICATION §11.4:
/// "Event explorer (filter <b>security/order/menu</b> events by subject, actor, type, and time)").
///
/// <para>These are query-local discriminators, not stored values — no column anywhere holds the word
/// <c>security</c>. They live in a named type for the same reason <c>OrderEventVocabulary</c>'s
/// operation kinds do: the reader projects them as literals and the surface switches on them, and a
/// disagreement between the two is a silent blank badge rather than a compile error. Unlike that type
/// this one is <c>public</c>, because the surface genuinely needs the words.</para>
/// </summary>
public static class EventStream
{
    /// <summary>The <c>security_event</c> table (§8.2, §3.4–§3.7). Subject: a person.</summary>
    public const string Security = "security";

    /// <summary>The <c>order_event</c> table (§6.2, §8.2). Subject: one guest's order.</summary>
    public const string Order = "order";

    /// <summary>The <c>menu_item_event</c> table (§7, §8.2). Subject: a menu item.</summary>
    public const string Menu = "menu";

    /// <summary>All three, in the order the explorer's filter offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Security, Order, Menu];

    /// <summary>True when <paramref name="stream"/> is one of the three (case-sensitive).</summary>
    public static bool IsKnown(string stream)
        => stream is Security or Order or Menu;
}

/// <summary>
/// Every event type the three streams' CHECK constraints admit, grouped by stream — the list the
/// explorer's type filter offers (TECHNICAL_SPECIFICATION §8.2, §11.4).
///
/// <para><b>This is a filter catalogue, not a mapping.</b> §11.4 requires administration to render "the
/// complete stored record … never projected or truncated", and every reader here obeys that by carrying
/// the stored <c>event_type</c> string through untouched — an event whose type is not in this list still
/// appears in the list, still with its own word. What the catalogue bounds is only which words the
/// dropdown offers, so an administrator picking from a menu cannot ask a question with a typo in
/// it.</para>
///
/// <para><b>The three vocabularies do not overlap</b>, which is what lets the filter be a single flat
/// <c>event_type = @EventType</c> across the union rather than a (stream, type) pair. That is a property
/// of the schema rather than a coincidence worth relying on silently, so
/// <c>EventExplorerReadsTests</c> asserts it directly.</para>
///
/// <para>The order and security lists are built from the constants their owners already declare —
/// <see cref="OrderEventVocabulary"/> (internal to this assembly) and
/// <see cref="SecurityEventType"/> (in the domain) — so neither can drift from its writer. The menu's
/// five words have no such home: <c>DapperMenuAdministration</c> and <c>DapperMenuAvailability</c> each
/// hold their own <c>private const</c>s, and hoisting them is a refactor of two files that this slice
/// does not need. They are therefore spelled here, and a container-backed test writes one event of each
/// type and asserts the explorer surfaces exactly these five.</para>
/// </summary>
public static class EventTypeCatalogue
{
    /// <summary>
    /// The nineteen <c>security_event.event_type</c> values (§8.2), in the order the schema lists them —
    /// which is also the order they group naturally for a reader: lifecycle, passwords, TOTP, passkeys,
    /// roles, sign-ins.
    /// </summary>
    public static IReadOnlyList<string> SecurityEventTypes { get; } =
    [
        SecurityEventType.AccountCreated,
        SecurityEventType.AccountDeactivated,
        SecurityEventType.AccountReactivated,
        SecurityEventType.PasswordChanged,
        SecurityEventType.PasswordResetByAdministrator,
        SecurityEventType.ForcedPasswordChangeCompleted,
        SecurityEventType.TotpEnrolled,
        SecurityEventType.TotpRemoved,
        SecurityEventType.TotpClearedByAdministrator,
        SecurityEventType.ForcedTotpEnrollmentCompleted,
        SecurityEventType.RecoveryCodeUsed,
        SecurityEventType.RecoveryCodesRegenerated,
        SecurityEventType.PasskeyRegistered,
        SecurityEventType.PasskeyRemoved,
        SecurityEventType.RoleGranted,
        SecurityEventType.RoleRevoked,
        SecurityEventType.SignInSucceeded,
        SecurityEventType.SignInFailed,
        SecurityEventType.AccountLockedOut,
    ];

    /// <summary>The five <c>order_event.event_type</c> values (§6.2, §8.2).</summary>
    public static IReadOnlyList<string> OrderEventTypes { get; } =
    [
        OrderEventVocabulary.GuestSubmission,
        OrderEventVocabulary.StaffEdit,
        OrderEventVocabulary.PriceAdjustment,
        OrderEventVocabulary.Fulfillment,
        OrderEventVocabulary.FulfillmentReversal,
    ];

    /// <summary>The five <c>menu_item_event.event_type</c> values (§7, §8.2).</summary>
    public static IReadOnlyList<string> MenuEventTypes { get; } =
    [
        "created",
        "name_changed",
        "price_changed",
        "activated",
        "deactivated",
    ];

    /// <summary>Every type the dropdown offers, security then order then menu.</summary>
    public static IReadOnlyList<string> All { get; } = SecurityEventTypes
        .Concat(OrderEventTypes)
        .Concat(MenuEventTypes)
        .ToArray();

    /// <summary>
    /// True when the type is one of the catalogued words. The explorer uses this only to tell an
    /// administrator that a hand-edited <c>?type=</c> is not a word this build knows about — it never
    /// refuses the filter, because a schema this build has not caught up with is exactly the case where
    /// somebody needs to look.
    /// </summary>
    public static bool IsKnown(string eventType) => All.Contains(eventType, StringComparer.Ordinal);

    /// <summary>
    /// Which stream a catalogued type belongs to, or <c>null</c> for a word not in the catalogue. Used to
    /// group the dropdown and to notice when a chosen type cannot possibly appear because its stream is
    /// switched off.
    /// </summary>
    public static string? StreamFor(string eventType)
    {
        if (SecurityEventTypes.Contains(eventType, StringComparer.Ordinal))
        {
            return EventStream.Security;
        }

        if (OrderEventTypes.Contains(eventType, StringComparer.Ordinal))
        {
            return EventStream.Order;
        }

        return MenuEventTypes.Contains(eventType, StringComparer.Ordinal)
            ? EventStream.Menu
            : null;
    }
}

/// <summary>
/// One event from any of the three streams, in the one shape the explorer renders
/// (TECHNICAL_SPECIFICATION §11.4).
///
/// <para><see cref="EventType"/>, <see cref="ActorRole"/> and <see cref="Stream"/> are strings rather
/// than enums, the same decision <see cref="Sittings.StoredOrderEvent"/>,
/// <see cref="Menu.MenuItemEventEntry"/> and <see cref="Orders.OrderVisibilityEntry"/> made and for the
/// same reason: an enum is a projection with a failure mode, and the one reader whose entire job is to
/// show what is actually in the tables is the last place a stored word may be mapped to something it is
/// not. The surface renders a friendly label for the words §8.2's CHECKs admit and falls back to the raw
/// string for anything else.</para>
///
/// <para><b>The nullable members are per-stream, not per-row accidents.</b> Only a security event may
/// have no actor (<c>actor_person_identifier</c> is the one nullable actor column in the three tables —
/// §8.2: NULL means the subject acted on themselves, or the system did). Only an order event carries a
/// <see cref="SequenceNumber"/> and an <see cref="ActorRole"/>. Only a menu event carries
/// <see cref="NewName"/> or <see cref="NewPriceAmount"/>, and then only on the types §8.2's paired
/// CHECKs allow each on.</para>
/// </summary>
/// <param name="Stream">Which table it came from — one of <see cref="EventStream"/>'s three words.</param>
/// <param name="EventIdentifier">The event row's own UUIDv7 primary key (ADR-0011).</param>
/// <param name="EventType">The stored <c>event_type</c>, untranslated.</param>
/// <param name="OccurredAt">When, in UTC (rendered in the restaurant's zone by the surface, §8.1).</param>
/// <param name="SubjectIdentifier">What the event is about: a person, a guest order, or a menu item, according to <see cref="Stream"/>.</param>
/// <param name="SubjectLabel">That subject named — the person's display name falling back to their username, the order owner's likewise, or the item's current name.</param>
/// <param name="SubjectDetail">A second line for the subject: the person's username, the order's table label, or <c>null</c> for a menu item, whose name is already the whole of it.</param>
/// <param name="ContextIdentifier">The sitting an order event happened in, so the row can link to the record that holds it (§11.4); <c>null</c> for the other two streams.</param>
/// <param name="ActorIdentifier">Who did it, or <c>null</c> on a security event with no actor.</param>
/// <param name="ActorName">Their display name falling back to their username, or <c>null</c> on the same terms.</param>
/// <param name="ActorRole">The capacity an order event's actor acted in (§8.2); <c>null</c> off that stream.</param>
/// <param name="SequenceNumber">An order event's per-order monotonic position (§6.6); <c>null</c> off that stream.</param>
/// <param name="NewName">The name a menu event set; <c>null</c> off that stream or off the types that carry one.</param>
/// <param name="NewPriceAmount">The price a menu event set, on the same terms.</param>
public sealed record ExplorerEvent(
    string Stream,
    Guid EventIdentifier,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid SubjectIdentifier,
    string SubjectLabel,
    string? SubjectDetail,
    Guid? ContextIdentifier,
    Guid? ActorIdentifier,
    string? ActorName,
    string? ActorRole,
    long? SequenceNumber,
    string? NewName,
    decimal? NewPriceAmount);

/// <summary>
/// How an administrator narrows the event explorer (TECHNICAL_SPECIFICATION §11.4: "filter
/// security/order/menu events by <b>subject, actor, type, and time</b>"; "filters narrow only on
/// explicit request").
///
/// <para><see cref="Everything"/> is the unfiltered default and is the state the screen opens in. That
/// is §11.4's rule rather than a convenience: a default of "today" or "security only" would quietly
/// answer a different question from the one the administrator asked, and they would have no way to know
/// it had.</para>
///
/// <para>The three stream flags are what makes "which of the three" a first-class filter rather than a
/// type list somebody has to know by heart. All three false is <em>not</em> the same as all three true:
/// it asks for nothing, and gets nothing. The surface never produces it — an empty checkbox set on a GET
/// form is indistinguishable from a fresh URL, so the page reads "none chosen" as "all three" before it
/// ever builds a filter — but the type admits it, so the reader answers it honestly.</para>
///
/// <para>The time range is half-open (<c>&gt;= from</c>, <c>&lt; before</c>) for the reason
/// <see cref="Orders.HiddenOrderFilter"/> gives: a caller turning a calendar day in the restaurant's zone
/// into a range passes the start of that day and the start of the next, and never has to decide whether
/// 23:59:59.999999 is inside it.</para>
/// </summary>
/// <param name="IncludeSecurityEvents">Whether to read <c>security_event</c>.</param>
/// <param name="IncludeOrderEvents">Whether to read <c>order_event</c>.</param>
/// <param name="IncludeMenuEvents">Whether to read <c>menu_item_event</c>.</param>
/// <param name="Subject">A substring of the subject's searchable text, or <c>null</c> for every subject. Case-insensitive; <c>%</c>, <c>_</c> and <c>\</c> match literally. The searchable text is the person's username and display name for a security event, the order owner's username and display name plus the table's label for an order event, and the item's name for a menu event.</param>
/// <param name="Actor">A substring of the actor's username or display name, on the same terms. An event with no actor never matches a set filter.</param>
/// <param name="EventType">One exact stored <c>event_type</c>, or <c>null</c> for every type. Exact rather than a substring because the words are a closed vocabulary and a substring of one is a different question — <c>totp_removed</c> is a substring of nothing, but <c>role_granted</c> and <c>role_revoked</c> share a prefix that a careless partial match would conflate.</param>
/// <param name="OccurredFrom">Only events at or after this instant, or <c>null</c> for no lower bound.</param>
/// <param name="OccurredBefore">Only events strictly before this instant, or <c>null</c> for no upper bound.</param>
public sealed record EventExplorerFilter(
    bool IncludeSecurityEvents = true,
    bool IncludeOrderEvents = true,
    bool IncludeMenuEvents = true,
    string? Subject = null,
    string? Actor = null,
    string? EventType = null,
    DateTimeOffset? OccurredFrom = null,
    DateTimeOffset? OccurredBefore = null)
{
    /// <summary>Every event in the restaurant — the state the §11.4 screen opens in.</summary>
    public static EventExplorerFilter Everything { get; } = new();

    /// <summary>
    /// True when at least one bound is set, which is what the page says out loud above the list. Turning
    /// a stream off counts: it is the coarsest narrowing there is, and a page that called itself
    /// unfiltered while showing only the menu would be lying in the one place §11.4 cares about.
    /// </summary>
    public bool IsNarrowed
        => !IncludeSecurityEvents
           || !IncludeOrderEvents
           || !IncludeMenuEvents
           || !string.IsNullOrWhiteSpace(Subject)
           || !string.IsNullOrWhiteSpace(Actor)
           || !string.IsNullOrWhiteSpace(EventType)
           || OccurredFrom is not null
           || OccurredBefore is not null;

    /// <summary>True when no stream is selected, so the answer is empty before any query runs.</summary>
    public bool IncludesNoStream
        => !IncludeSecurityEvents && !IncludeOrderEvents && !IncludeMenuEvents;
}

/// <summary>
/// The §11.4 event explorer's read side: one question asked of all three append-only logs at once.
///
/// <para><b>Why a fourth reader rather than three calls.</b> The engines already exist —
/// <see cref="Sittings.ISittingRecordReads"/> for order events, <see cref="Menu.IMenuEventLog"/> for menu
/// events, and the <c>security_event</c> table that <see cref="Identity.ISecurityEventLog"/> writes to.
/// But each is scoped to one subject the caller already names: one sitting, one item, one account. The
/// explorer's question is the opposite shape — "what happened, anywhere, in this window" — and answering
/// it by reading three lists and merging them in memory would mean fetching every event in the
/// restaurant to render the fifty most recent. The interleaving, the ordering, and the cap have to
/// happen in one statement, so the statement has to exist.</para>
///
/// <para><b>Three streams, not four.</b> §6.8's <c>order_visibility_event</c> is deliberately absent:
/// §11.4 names security, order, and menu, and gives visibility its own screen — the hidden-records view,
/// where a visibility log sits beside the order it is about and next to the Unhide button that is its
/// only counterpart. Folding it in here would put a row saying "somebody tidied their history" in the
/// same list as the meal itself, without the one control that answers it.</para>
///
/// <para><b>Nothing is projected.</b> Every stored word — <c>event_type</c>, <c>actor_role</c> — comes
/// back as itself, and the reader is incapable of throwing on a value it does not recognise. That is
/// §11.4's "complete stored record … never projected or truncated" applied to the one screen whose
/// entire purpose is to show what the tables say.</para>
/// </summary>
public interface IEventExplorerReads
{
    /// <summary>
    /// Every event matching <paramref name="filter"/>, newest first, capped at
    /// <paramref name="maximumCount"/>.
    ///
    /// <para>Newest first because the question is nearly always "what just happened"; capped because the
    /// three logs together are the busiest thing in the database and an uncapped read of them is a page
    /// that never finishes. The cap is a <em>rendering</em> bound, not a filter — the surface says when
    /// it has been reached and points at the filter rather than letting a round number pass for an
    /// answer.</para>
    ///
    /// <para>Ordering is <c>(occurred_at DESC, event_identifier DESC)</c>. The identifier tiebreak makes
    /// a re-read deterministic across streams: all three primary keys are UUIDv7 (ADR-0011), so two
    /// events that share an instant — which they do whenever one transaction writes a row and its event
    /// together — still have a total order, and paging past the cap by narrowing the window cannot
    /// silently skip or repeat one.</para>
    ///
    /// <para>A non-positive cap, and a filter selecting no stream at all, both answer with nothing and
    /// without a round trip.</para>
    /// </summary>
    Task<IReadOnlyList<ExplorerEvent>> ListAsync(
        EventExplorerFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IEventExplorerReads"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (this is a lone read), columns
/// aliased to the record's member names, every column reference table-qualified — all three event tables
/// carry an <c>event_type</c> and an <c>occurred_at</c>, and <c>person</c> is joined twice in one branch,
/// so an unqualified reference is exactly how PostgreSQL error 42702 bites — and rows read into an
/// internal row type whose members match what Npgsql actually returns before being projected, because
/// <c>timestamptz</c> arrives as <see cref="DateTime"/> and Dapper's constructor binding will not feed
/// one into a <see cref="DateTimeOffset"/> parameter.
/// </summary>
public sealed class DapperEventExplorerReads : IEventExplorerReads
{
    /// <summary>
    /// The escape character for the two <c>ILIKE</c> patterns. A backslash is the conventional choice
    /// and, with <c>standard_conforming_strings</c> on (the default since PostgreSQL 9.1), <c>'\'</c> in
    /// the SQL below is a single literal backslash rather than the start of an escape sequence — the same
    /// reasoning, and the same character, as <see cref="Orders.DapperOrderHistoryReads"/>.
    /// </summary>
    private const char LikeEscape = '\\';

    /// <summary>
    /// The three streams as one flat result, then filtered, ordered, and capped once.
    ///
    /// <para><b>Every column is aliased in every branch</b>, unlike the five-way union in
    /// <c>DapperSittingRecordReads</c>, which aliases only its first. PostgreSQL takes the column names
    /// from the first branch and ignores the rest, so the aliases below the first are documentation — and
    /// with sixteen columns drawn from three unrelated tables, documentation is exactly what is needed to
    /// keep a future edit from inserting a column into one branch and silently shifting five others.</para>
    ///
    /// <para><b>Missing columns are cast, not bare.</b> <c>NULL::uuid</c>, <c>NULL::bigint</c>,
    /// <c>NULL::text</c>, <c>NULL::numeric(10,2)</c>: in a union PostgreSQL resolves each column's type
    /// from the branches, and a bare <c>NULL</c> would leave it <c>unknown</c>. Every
    /// <c>citext</c> is likewise cast to <c>text</c> before it meets a <c>text</c> from another branch, so
    /// no column's type depends on how the planner resolves <c>citext</c> against <c>text</c>.</para>
    ///
    /// <para><b>The stream flags sit inside the branches, the rest of the filter outside.</b> A stream
    /// that is switched off is not scanned at all; the other five bounds are written once against the
    /// union's output rather than three times against its inputs. PostgreSQL pushes those qualifiers back
    /// down into the branches on its own, so the shorter version costs nothing and cannot drift between
    /// copies — which is the failure the sitting-record reader's shared WHERE fragments exist to
    /// prevent.</para>
    ///
    /// <para><b>Each bound is <c>@Parameter IS NULL OR …</c></b>, so one statement serves all
    /// thirty-two combinations. The alternative — composing the WHERE clause in C# — is how a reader ends
    /// up with a code path nobody tested.</para>
    ///
    /// <para>The two search columns are <c>concat_ws</c> rather than <c>||</c>: it skips NULLs instead of
    /// annihilating the whole expression, so a person with no display name is still findable by username.
    /// For a security event with no actor it yields the empty string, which matches no set actor filter —
    /// correct, and quieter than a NULL that would need a COALESCE at every use.</para>
    /// </summary>
    private static readonly string ExplorerSql = $"""
        SELECT event_row.event_stream       AS Stream,
               event_row.event_identifier   AS EventIdentifier,
               event_row.event_type         AS EventType,
               event_row.occurred_at        AS OccurredAt,
               event_row.subject_identifier AS SubjectIdentifier,
               event_row.subject_label      AS SubjectLabel,
               event_row.subject_detail     AS SubjectDetail,
               event_row.context_identifier AS ContextIdentifier,
               event_row.actor_identifier   AS ActorIdentifier,
               event_row.actor_name         AS ActorName,
               event_row.actor_role         AS ActorRole,
               event_row.sequence_number    AS SequenceNumber,
               event_row.new_name           AS NewName,
               event_row.new_price_amount   AS NewPriceAmount
        FROM (
            SELECT '{EventStream.Security}'::text            AS event_stream,
                   security_event.security_event_identifier  AS event_identifier,
                   security_event.event_type                 AS event_type,
                   security_event.occurred_at                AS occurred_at,
                   security_event.subject_person_identifier  AS subject_identifier,
                   COALESCE(NULLIF(btrim(subject.display_name), ''), subject.username::text)
                                                             AS subject_label,
                   subject.username::text                    AS subject_detail,
                   NULL::uuid                                AS context_identifier,
                   security_event.actor_person_identifier    AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   NULL::text                                AS actor_role,
                   NULL::bigint                              AS sequence_number,
                   NULL::text                                AS new_name,
                   NULL::numeric(10,2)                       AS new_price_amount,
                   concat_ws(' ', subject.username::text, subject.display_name)
                                                             AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM security_event
            INNER JOIN person AS subject
                    ON subject.person_identifier = security_event.subject_person_identifier
            LEFT JOIN person AS actor
                    ON actor.person_identifier = security_event.actor_person_identifier
            WHERE @IncludeSecurityEvents::boolean

            UNION ALL

            SELECT '{EventStream.Order}'::text               AS event_stream,
                   order_event.order_event_identifier        AS event_identifier,
                   order_event.event_type                    AS event_type,
                   order_event.occurred_at                   AS occurred_at,
                   order_event.guest_order_identifier        AS subject_identifier,
                   COALESCE(NULLIF(btrim(owner.display_name), ''), owner.username::text)
                                                             AS subject_label,
                   restaurant_table.label                    AS subject_detail,
                   guest_order.table_sitting_identifier      AS context_identifier,
                   order_event.actor_person_identifier       AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   order_event.actor_role                    AS actor_role,
                   order_event.sequence_number               AS sequence_number,
                   NULL::text                                AS new_name,
                   NULL::numeric(10,2)                       AS new_price_amount,
                   concat_ws(' ', owner.username::text, owner.display_name, restaurant_table.label)
                                                             AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM order_event
            INNER JOIN guest_order
                    ON guest_order.guest_order_identifier = order_event.guest_order_identifier
            INNER JOIN table_sitting
                    ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
            INNER JOIN restaurant_table
                    ON restaurant_table.restaurant_table_identifier
                       = table_sitting.restaurant_table_identifier
            INNER JOIN person AS owner
                    ON owner.person_identifier = guest_order.person_identifier
            INNER JOIN person AS actor
                    ON actor.person_identifier = order_event.actor_person_identifier
            WHERE @IncludeOrderEvents::boolean

            UNION ALL

            SELECT '{EventStream.Menu}'::text                AS event_stream,
                   menu_item_event.menu_item_event_identifier
                                                             AS event_identifier,
                   menu_item_event.event_type                AS event_type,
                   menu_item_event.occurred_at               AS occurred_at,
                   menu_item_event.menu_item_identifier      AS subject_identifier,
                   menu_item.name                            AS subject_label,
                   NULL::text                                AS subject_detail,
                   NULL::uuid                                AS context_identifier,
                   menu_item_event.actor_person_identifier   AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   NULL::text                                AS actor_role,
                   NULL::bigint                              AS sequence_number,
                   menu_item_event.new_name                  AS new_name,
                   menu_item_event.new_price_amount          AS new_price_amount,
                   menu_item.name                            AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM menu_item_event
            INNER JOIN menu_item
                    ON menu_item.menu_item_identifier = menu_item_event.menu_item_identifier
            INNER JOIN person AS actor
                    ON actor.person_identifier = menu_item_event.actor_person_identifier
            WHERE @IncludeMenuEvents::boolean
        ) AS event_row
        WHERE (@SubjectPattern IS NULL
               OR event_row.subject_search ILIKE @SubjectPattern ESCAPE '{LikeEscape}')
          AND (@ActorPattern IS NULL
               OR event_row.actor_search ILIKE @ActorPattern ESCAPE '{LikeEscape}')
          AND (@EventType IS NULL OR event_row.event_type = @EventType)
          AND (@OccurredFrom IS NULL OR event_row.occurred_at >= @OccurredFrom)
          AND (@OccurredBefore IS NULL OR event_row.occurred_at < @OccurredBefore)
        ORDER BY event_row.occurred_at DESC, event_row.event_identifier DESC
        LIMIT @MaximumCount;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperEventExplorerReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ExplorerEvent>> ListAsync(
        EventExplorerFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Asking for nothing is answered without a round trip rather than by an empty LIMIT 0 or by
        // three sequential scans whose union is discarded.
        if (maximumCount <= 0 || filter.IncludesNoStream)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ExplorerRow> rows = await connection
            .QueryAsync<ExplorerRow>(new CommandDefinition(
                ExplorerSql,
                new
                {
                    filter.IncludeSecurityEvents,
                    filter.IncludeOrderEvents,
                    filter.IncludeMenuEvents,
                    SubjectPattern = SubstringPattern(filter.Subject),
                    ActorPattern = SubstringPattern(filter.Actor),
                    EventType = NullIfBlank(filter.EventType),
                    filter.OccurredFrom,
                    filter.OccurredBefore,
                    MaximumCount = maximumCount,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(row => new ExplorerEvent(
            row.Stream,
            row.EventIdentifier,
            row.EventType,
            AsUtc(row.OccurredAt),
            row.SubjectIdentifier,
            row.SubjectLabel,
            row.SubjectDetail,
            row.ContextIdentifier,
            row.ActorIdentifier,
            row.ActorName,
            row.ActorRole,
            row.SequenceNumber,
            row.NewName,
            row.NewPriceAmount)).ToArray();
    }

    /// <summary>
    /// Turns a search term into an <c>ILIKE</c> pattern that matches it as a literal substring. The three
    /// characters <c>LIKE</c> gives meaning to — <c>\</c>, <c>%</c>, <c>_</c> — are escaped, so searching
    /// for <c>a_b</c> finds <c>a_b</c> and not <c>axb</c>. Whitespace-only input is no filter at all
    /// rather than a pattern matching everything: the same answer, but one that says so, and one that
    /// keeps <c>IsNarrowed</c> honest. Identical in behaviour to
    /// <see cref="Orders.DapperOrderHistoryReads"/>'s, which is deliberate — two search boxes on two
    /// administration screens that escaped differently would be a bug nobody could see.
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

    /// <summary>
    /// A blank type is no type filter. Whitespace would otherwise be sent as an exact match no stored
    /// word can satisfy, which reads to an administrator as "there are no events" rather than as "that
    /// box is empty".
    /// </summary>
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Dapper binds this positional record by constructor-parameter name against the aliased columns
    // above; every member's CLR type matches exactly what Npgsql returns for that PostgreSQL type
    // (text → string, uuid → Guid, bigint → long, numeric → decimal, timestamptz → DateTime), because
    // Dapper's constructor binding does not convert.
    private sealed record ExplorerRow(
        string Stream,
        Guid EventIdentifier,
        string EventType,
        DateTime OccurredAt,
        Guid SubjectIdentifier,
        string SubjectLabel,
        string? SubjectDetail,
        Guid? ContextIdentifier,
        Guid? ActorIdentifier,
        string? ActorName,
        string? ActorRole,
        long? SequenceNumber,
        string? NewName,
        decimal? NewPriceAmount);
}
