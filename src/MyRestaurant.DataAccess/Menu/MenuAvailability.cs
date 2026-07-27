using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>What happened to a request to flip a menu item's availability (§7, §11.2).</summary>
public enum SetMenuItemAvailabilityOutcome
{
    /// <summary>The flag moved and a <c>menu_item_event</c> was written.</summary>
    Changed,

    /// <summary>The item was already in the requested state; nothing was written.</summary>
    AlreadyInThatState,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>
/// The outcome of one availability flip, carrying the item's name and current flag so a caller can say
/// "Salmon is 86'd" without a second read.
/// </summary>
public sealed record SetMenuItemAvailabilityResult(
    SetMenuItemAvailabilityOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    bool IsActive)
{
    /// <summary>True when the flag actually moved — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Changed => Outcome is SetMenuItemAvailabilityOutcome.Changed;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not SetMenuItemAvailabilityOutcome.MenuItemNotFound;
}

/// <summary>
/// The "86" write (TECHNICAL_SPECIFICATION §7, §11.2: "an '86' panel lists menu items with active
/// toggles"). It is the one piece of menu <em>administration</em> that could not wait for M5, because
/// §11.2 puts the toggle on the kitchen board and the kitchen is the surface that knows the salmon has
/// run out.
///
/// <para>Availability only — no create, rename, or reprice. Those are the rest of §11.4's menu CRUD and
/// arrive with M5 alongside the per-item event history that reads what this writes. Keeping the write
/// this narrow means the kitchen board cannot become an accidental menu editor, and it means M5's
/// interface can be designed for the whole job rather than grown out of this one.</para>
///
/// <para>Deactivating is <b>not</b> deleting and does not hide anything: §7 requires a deactivated item
/// to stay on the menu marked "currently unavailable", and lines already added keep the price and the
/// item they were added with. The only behaviour that changes is that the order-mutating transaction
/// refuses to add a new line for it (§6.5.4), re-reading this flag under its own lock.</para>
/// </summary>
public interface IMenuAvailability
{
    /// <summary>
    /// Sets one item's <c>is_active</c> flag and appends the matching <c>menu_item_event</c>
    /// (<c>activated</c> / <c>deactivated</c>) in one transaction, or reports that there was nothing to
    /// do. A no-op flip writes no event: an append-only log of "somebody pressed a button that changed
    /// nothing" is noise, and §11.4's per-item history is meant to be read by a person.
    /// </summary>
    Task<SetMenuItemAvailabilityResult> SetActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuAvailability"/>. One connection and one transaction per
/// operation, one <see cref="IClock.UtcNow"/> instant on both rows, a UUIDv7 event key from
/// <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this layer has.
///
/// <para>The row is taken <c>FOR UPDATE</c> before it is compared. Without the lock, two staff toggling
/// the same item at once could both read "active", both write "inactive", and log two
/// <c>deactivated</c> events for one deactivation — the flag would still be right and the history would
/// be a lie, which is the worse of the two failures in an append-only system (ADR-0002).</para>
/// </summary>
public sealed class DapperMenuAvailability : IMenuAvailability
{
    /// <summary>Stored spellings of <c>menu_item_event.event_type</c> (§8.2's CHECK).</summary>
    private const string ActivatedEventType = "activated";

    private const string DeactivatedEventType = "deactivated";

    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier AS MenuItemIdentifier,
               menu_item.name                 AS Name,
               menu_item.is_active            AS IsActive
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string UpdateActiveSql = """
        UPDATE menu_item
        SET is_active = @IsActive
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    // new_name and new_price_amount must be NULL for these two event types — the §8.2 paired CHECKs
    // tie each nullable column to exactly the event types that carry it.
    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, NULL, NULL, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuAvailability(
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

    public async Task<SetMenuItemAvailabilityResult> SetActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await connection
            .QuerySingleOrDefaultAsync<MenuItemLockRow>(new CommandDefinition(
                LockMenuItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SetMenuItemAvailabilityResult(
                SetMenuItemAvailabilityOutcome.MenuItemNotFound,
                menuItemIdentifier,
                Name: null,
                IsActive: false);
        }

        if (item.IsActive == isActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SetMenuItemAvailabilityResult(
                SetMenuItemAvailabilityOutcome.AlreadyInThatState,
                menuItemIdentifier,
                item.Name,
                item.IsActive);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateActiveSql,
            new { MenuItemIdentifier = menuItemIdentifier, IsActive = isActive },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemEventSql,
            new
            {
                MenuItemEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = isActive ? ActivatedEventType : DeactivatedEventType,
                OccurredAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SetMenuItemAvailabilityResult(
            SetMenuItemAvailabilityOutcome.Changed,
            menuItemIdentifier,
            item.Name,
            isActive);
    }

    private sealed record MenuItemLockRow(Guid MenuItemIdentifier, string Name, bool IsActive);
}
