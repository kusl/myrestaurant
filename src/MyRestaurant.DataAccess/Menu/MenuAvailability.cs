using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

public enum SetMenuItemAvailabilityOutcome
{
    Changed,
    AlreadyInThatState,
    MenuItemNotFound,
}

public sealed record SetMenuItemAvailabilityResult(
    SetMenuItemAvailabilityOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    bool IsActive)
{
    public bool Changed => Outcome is SetMenuItemAvailabilityOutcome.Changed;

    public bool ItemExists => Outcome is not SetMenuItemAvailabilityOutcome.MenuItemNotFound;
}

public interface IMenuAvailability
{
    Task<SetMenuItemAvailabilityResult> SetActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuAvailability : IMenuAvailability
{
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
