using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// How many people currently like one dish (TECHNICAL_SPECIFICATION §7, §8.3, §11.4).
///
/// <para><b>"Currently" is the whole of it.</b> The number is a count over
/// <c>menu_item_reaction_current</c>, which holds one row per person per dish — their <em>last</em>
/// press. Somebody who liked a dish in March and unliked it in April contributes nothing, and somebody
/// who liked it three times contributes one. The plausible wrong implementation counts <c>'liked'</c>
/// rows in the event table, which is a count of presses rather than of people and which grows every time
/// anybody changes their mind.</para>
/// </summary>
/// <param name="MenuItemIdentifier">The dish.</param>
/// <param name="LikeCount">People who currently like it. Never zero — an item nobody likes is absent from the list rather than present with a zero, because the caller already holds the menu (§11.4).</param>
public sealed record MenuItemLikeCount(Guid MenuItemIdentifier, int LikeCount);

/// <summary>
/// Reads what people think of the menu (TECHNICAL_SPECIFICATION §7, §8.3). The read side only; the press
/// itself is <see cref="IMenuItemReactions"/>, exactly as <see cref="IMenuDirectory"/> stands beside
/// <see cref="IMenuAdministration"/>.
///
/// <para><b>Two reads, because there are two questions and they belong to different people.</b>
/// <see cref="ListLikeCountsAsync"/> is "which of these is popular", which §11.4's administrator asks
/// about the whole menu; <see cref="ListLikedByAsync"/> is "which of these do <em>I</em> like", which
/// §11.1's guest asks about their own presses so the control can render the state it is in. A single
/// read returning both would hand every guest the whole restaurant's opinion, which is the thing Stage 5
/// ruled against.</para>
///
/// <para><b>Both are whole-menu reads and neither takes an item.</b> A per-item lookup inside a render
/// loop turns a sixty-dish menu into sixty queries, which is the argument
/// <see cref="IMenuItemImageDirectory.ListAsync"/> already carries and the reason it exists in that
/// shape.</para>
///
/// <para><b>Neither has a caller yet, and that is Stage 5a's stated position rather than an
/// oversight.</b> Stage 5b builds the two surfaces. A read with no caller is the weaker of this
/// project's two "no caller" defects — an unread read cannot change anything without telling anybody —
/// and it is the same position <see cref="IMenuItemImageDirectory"/> was in for three slices after
/// <c>0006</c>.</para>
/// </summary>
public interface IMenuItemReactionDirectory
{
    /// <summary>
    /// Every dish anybody currently likes, with the number of people who do, ordered by identifier so
    /// the result is stable across calls. Dishes nobody likes are simply absent — this is not a left
    /// join over <c>menu_item</c>, because the caller already holds the menu and wants to know which of
    /// it is liked.
    /// </summary>
    Task<IReadOnlyList<MenuItemLikeCount>> ListLikeCountsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every dish one person currently likes, ordered by identifier. An unknown person and a person who
    /// likes nothing are the same answer — an empty list — deliberately: the caller is a surface
    /// rendering for a principal that has already been authenticated, so a second null-ish outcome would
    /// make every call site handle a case it cannot reach.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListLikedByAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>What happened to one press (§7, §11.1).</summary>
public enum SetMenuItemReactionOutcome
{
    /// <summary>The opinion moved and a <c>menu_item_reaction_event</c> row was written.</summary>
    Changed,

    /// <summary>The person already thought that; nothing was written.</summary>
    AlreadyInThatState,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>
/// The outcome of one press, carrying the state the person is now in so a caller can render the control
/// without a second read.
/// </summary>
public sealed record SetMenuItemReactionResult(
    SetMenuItemReactionOutcome Outcome,
    Guid MenuItemIdentifier,
    Guid PersonIdentifier,
    bool IsLiked)
{
    /// <summary>True when a row was written.</summary>
    public bool Changed => Outcome is SetMenuItemReactionOutcome.Changed;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not SetMenuItemReactionOutcome.MenuItemNotFound;
}

/// <summary>
/// One person's opinion of one dish (TECHNICAL_SPECIFICATION §7, §8.2, §8.3; Stage 5a of
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>This is the first menu write in this tree that is not behind <see cref="IMenuWorkflow"/>,
/// and that is a ruling rather than an omission.</b> The workflow exists so that a change to the menu is
/// announced (§9) — a reprice nobody announced quotes a guest a price nobody charges, and an 86 nobody
/// announced leaves a dish tappable on every open picker. A reaction changes nothing any surface renders
/// from the menu: the dish keeps its name, its price, its heading, its position, its availability and
/// its photograph. What it changes is a number only staff read and a control the presser is looking at,
/// and a static or interactive surface both re-render that for themselves. Publishing <c>MenuChanged</c>
/// here would make a heart-tap re-read the entire menu on every phone in the building — and this is the
/// one write in this application that can happen many times a minute at one table.</para>
///
/// <para><b>The person is the actor.</b> Every other write in this family takes an
/// <c>actorPersonIdentifier</c> distinct from its subject, because an administrator renames somebody
/// else's dish. Nobody presses this on another person's behalf and no surface in §11 could offer to, so
/// the one identifier is both — which is why <c>0008</c> declares no <c>actor_person_identifier</c>
/// column.</para>
///
/// <para><b>Nothing here asks whether the person ordered the dish, and Stage 5 ruled on that
/// explicitly.</b> <c>order_current_line</c> records what somebody <em>ordered</em>, not what they ate,
/// and a table shares — so the requirement would refuse the case it most wants to admit (I ate my
/// partner's dessert and it was the best thing on the menu) while admitting the one it wants to refuse.
/// It would also make a menu write read order history, inverting §6.5.4's direction. If the restriction
/// is ever wanted it belongs on the <em>read</em> as a second, narrower count, not here as a refusal a
/// guest at a table would have to be given a sentence about.</para>
/// </summary>
public interface IMenuItemReactions
{
    /// <summary>
    /// Records that a person does or does not like a dish, appending one
    /// <c>menu_item_reaction_event</c> row when that is a change and nothing when it is not.
    ///
    /// <para>A press that changes nothing writes nothing, on the no-op rule every other menu verb
    /// follows (§7): an append-only log of "somebody's thumb touched glass" is noise, and this is the
    /// table most able to fill with it — a double-tap is an ordinary gesture rather than an edge
    /// case.</para>
    /// </summary>
    Task<SetMenuItemReactionResult> SetLikedAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        bool isLiked,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuItemReactionDirectory"/>. Reads only: own connection per
/// call, no transaction, nothing locked — the shape <see cref="DapperMenuDirectory"/> and
/// <see cref="DapperMenuItemImageDirectory"/> both have.
///
/// <para><b>Both queries read the view rather than the event table</b>, which is the entire reason
/// <c>0008</c> declares one. The fold is where "this person's current opinion" is defined; a reader
/// assembling it from <c>menu_item_reaction_event</c> would be a second definition of it, and the
/// second definition is the one that counts presses instead of people.</para>
/// </summary>
public sealed class DapperMenuItemReactionDirectory : IMenuItemReactionDirectory
{
    // WHERE is_liked rather than WHERE event_type = 'liked': the view has already folded, so this
    // filters people's current opinions and not rows. count(*) is bigint in PostgreSQL, cast here
    // rather than widened in C# so the column and the record agree at the boundary.
    private const string ListLikeCountsSql = """
        SELECT menu_item_identifier AS MenuItemIdentifier,
               count(*)::integer    AS LikeCount
        FROM menu_item_reaction_current
        WHERE is_liked
        GROUP BY menu_item_identifier
        ORDER BY menu_item_identifier;
        """;

    private const string ListLikedByPersonSql = """
        SELECT menu_item_identifier
        FROM menu_item_reaction_current
        WHERE person_identifier = @PersonIdentifier
          AND is_liked
        ORDER BY menu_item_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuItemReactionDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemLikeCount>> ListLikeCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemLikeCount> rows = await connection
            .QueryAsync<MenuItemLikeCount>(new CommandDefinition(
                ListLikeCountsSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<Guid>> ListLikedByAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<Guid> rows = await connection
            .QueryAsync<Guid>(new CommandDefinition(
                ListLikedByPersonSql,
                new { PersonIdentifier = personIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToArray();
    }
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuItemReactions"/>. One connection and one transaction per
/// press, one <see cref="IClock.UtcNow"/> instant on the row, a UUIDv7 key from
/// <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this layer has.
///
/// <para><b>The <c>menu_item</c> row is taken <c>FOR UPDATE</c> before the comparison</b>, which is the
/// standing rule in this file's neighbours and is doing two jobs at once here: it answers
/// <see cref="SetMenuItemReactionOutcome.MenuItemNotFound"/> from the same statement that takes the
/// lock, and it serialises the read-compare-append against itself. Without it two presses from one
/// person could both read "not liked" and both append <c>'liked'</c> — the fold would still be right and
/// the history would be a lie, which is the worse of the two failures in an append-only system
/// (ADR-0002).</para>
///
/// <para><b>What that lock costs is stated rather than left to be discovered.</b> It is wider than the
/// conflict: the conflict is one person against themselves, and this serialises every press on one dish
/// against every other, and against a rename of it. The alternatives were an advisory lock keyed on the
/// pair, which is a second locking scheme in a codebase that has one, and no lock at all, which trades a
/// truthful log for concurrency nobody at one restaurant's table will ever need. A dish is locked for
/// the duration of one <c>INSERT</c>.</para>
///
/// <para><b>Absence and <c>'unliked'</c> are the same state, deliberately.</b> The fold has no row for a
/// person who has never pressed anything, so the read below coalesces to <c>false</c> — which means
/// unliking something never liked is <see cref="SetMenuItemReactionOutcome.AlreadyInThatState"/> and
/// writes nothing, rather than a fourth outcome nobody could act on. The alternative would put a
/// <c>'unliked'</c> row in the log recording a withdrawal of an opinion never held.</para>
/// </summary>
public sealed class DapperMenuItemReactions : IMenuItemReactions
{
    /// <summary>Stored spellings of <c>menu_item_reaction_event.event_type</c> (<c>0008</c>'s CHECK).</summary>
    private const string LikedEventType = "liked";

    private const string UnlikedEventType = "unliked";

    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    // Reads the fold, not the event table, and reads it inside the transaction so it sees this
    // transaction's own uncommitted rows — which is what makes a second press within one request
    // compare against the first.
    private const string ReadCurrentSql = """
        SELECT is_liked
        FROM menu_item_reaction_current
        WHERE menu_item_identifier = @MenuItemIdentifier
          AND person_identifier = @PersonIdentifier;
        """;

    private const string InsertReactionEventSql = """
        INSERT INTO menu_item_reaction_event (
            menu_item_reaction_event_identifier, menu_item_identifier,
            person_identifier, event_type, occurred_at)
        VALUES (
            @MenuItemReactionEventIdentifier, @MenuItemIdentifier,
            @PersonIdentifier, @EventType, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuItemReactions(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
    }

    public async Task<SetMenuItemReactionResult> SetLikedAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        bool isLiked,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid? item = await connection
            .ExecuteScalarAsync<Guid?>(new CommandDefinition(
                LockMenuItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SetMenuItemReactionResult(
                SetMenuItemReactionOutcome.MenuItemNotFound,
                menuItemIdentifier,
                personIdentifier,
                IsLiked: false);
        }

        bool? stored = await connection
            .ExecuteScalarAsync<bool?>(new CommandDefinition(
                ReadCurrentSql,
                new { MenuItemIdentifier = menuItemIdentifier, PersonIdentifier = personIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // No row means never pressed, which is the same opinion as 'unliked'. See the class summary:
        // the alternative writes a withdrawal of an opinion never held.
        bool currentlyLiked = stored ?? false;

        if (currentlyLiked == isLiked)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SetMenuItemReactionResult(
                SetMenuItemReactionOutcome.AlreadyInThatState,
                menuItemIdentifier,
                personIdentifier,
                currentlyLiked);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertReactionEventSql,
            new
            {
                MenuItemReactionEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                PersonIdentifier = personIdentifier,
                EventType = isLiked ? LikedEventType : UnlikedEventType,
                OccurredAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SetMenuItemReactionResult(
            SetMenuItemReactionOutcome.Changed,
            menuItemIdentifier,
            personIdentifier,
            isLiked);
    }
}
