using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemLikeCount(Guid MenuItemIdentifier, int LikeCount);

public interface IMenuItemReactionDirectory
{
    Task<IReadOnlyList<MenuItemLikeCount>> ListLikeCountsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListLikedByAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default);
}

public enum SetMenuItemReactionOutcome
{
    Changed,
    AlreadyInThatState,
    MenuItemNotFound,
}

public sealed record SetMenuItemReactionResult(
    SetMenuItemReactionOutcome Outcome,
    Guid MenuItemIdentifier,
    Guid PersonIdentifier,
    bool IsLiked)
{
    public bool Changed => Outcome is SetMenuItemReactionOutcome.Changed;

    public bool ItemExists => Outcome is not SetMenuItemReactionOutcome.MenuItemNotFound;
}

public interface IMenuItemReactions
{
    Task<SetMenuItemReactionResult> SetLikedAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        bool isLiked,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuItemReactionDirectory : IMenuItemReactionDirectory
{
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

public sealed class DapperMenuItemReactions : IMenuItemReactions
{
    private const string LikedEventType = "liked";

    private const string UnlikedEventType = "unliked";

    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

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
