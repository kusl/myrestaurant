using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Orders;

public enum HideOrderOutcome
{
    Hidden,
    OrderNotFound,
    NotTheOwner,
    SittingStillOpen,
    AlreadyHidden,
}

public enum UnhideOrderOutcome
{
    Unhidden,
    OrderNotFound,
    NotHidden,
}

public sealed record HideOrderResult(
    HideOrderOutcome Outcome,
    Guid GuestOrderIdentifier,
    Guid? SittingIdentifier,
    Guid? OwnerPersonIdentifier,
    DateTimeOffset? OccurredAt)
{
    public bool IsHidden => Outcome is HideOrderOutcome.Hidden;

    public bool OrderIsHidden => Outcome is HideOrderOutcome.Hidden or HideOrderOutcome.AlreadyHidden;
}

public sealed record UnhideOrderResult(
    UnhideOrderOutcome Outcome,
    Guid GuestOrderIdentifier,
    Guid? SittingIdentifier,
    Guid? OwnerPersonIdentifier,
    DateTimeOffset? OccurredAt)
{
    public bool IsUnhidden => Outcome is UnhideOrderOutcome.Unhidden;
}

public interface IOrderVisibility
{
    Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperOrderVisibility : IOrderVisibility
{
    private const string LockOrderSql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS OwnerPersonIdentifier,
               sitting.closed_at                    AS SittingClosedAt
        FROM guest_order
        INNER JOIN table_sitting AS sitting
                ON sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        WHERE guest_order.guest_order_identifier = @GuestOrderIdentifier
        FOR UPDATE OF guest_order;
        """;

    private const string CurrentFlagSql = """
        SELECT order_visibility_current.is_hidden
        FROM order_visibility_current
        WHERE order_visibility_current.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private const string AppendVisibilityEventSql = """
        INSERT INTO order_visibility_event (
            order_visibility_event_identifier,
            guest_order_identifier,
            actor_person_identifier,
            event_type,
            occurred_at)
        VALUES (
            @VisibilityEventIdentifier,
            @GuestOrderIdentifier,
            @ActorPersonIdentifier,
            @EventType,
            @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperOrderVisibility(
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

    public async Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        OrderLockRow? order = await LockOrderAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.OrderNotFound, guestOrderIdentifier, null, null, null);
        }

        if (order.OwnerPersonIdentifier != ownerPersonIdentifier)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.NotTheOwner,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        if (order.SittingClosedAt is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.SittingStillOpen,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        bool isHidden = await ReadCurrentFlagAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (isHidden)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.AlreadyHidden,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        await AppendAsync(
            connection,
            transaction,
            guestOrderIdentifier,
            ownerPersonIdentifier,
            OrderEventVocabulary.HiddenVisibility,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new HideOrderResult(
            HideOrderOutcome.Hidden,
            guestOrderIdentifier,
            order.SittingIdentifier,
            order.OwnerPersonIdentifier,
            now);
    }

    public async Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        OrderLockRow? order = await LockOrderAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UnhideOrderResult(
                UnhideOrderOutcome.OrderNotFound, guestOrderIdentifier, null, null, null);
        }

        bool isHidden = await ReadCurrentFlagAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (!isHidden)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UnhideOrderResult(
                UnhideOrderOutcome.NotHidden,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        await AppendAsync(
            connection,
            transaction,
            guestOrderIdentifier,
            administratorPersonIdentifier,
            OrderEventVocabulary.UnhiddenVisibility,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new UnhideOrderResult(
            UnhideOrderOutcome.Unhidden,
            guestOrderIdentifier,
            order.SittingIdentifier,
            order.OwnerPersonIdentifier,
            now);
    }

    private static async Task<OrderLockRow?> LockOrderAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken)
        => await connection
            .QuerySingleOrDefaultAsync<OrderLockRow>(new CommandDefinition(
                LockOrderSql,
                new { GuestOrderIdentifier = guestOrderIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<bool> ReadCurrentFlagAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken)
    {
        bool? isHidden = await connection
            .QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
                CurrentFlagSql,
                new { GuestOrderIdentifier = guestOrderIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return isHidden ?? false;
    }

    private async Task AppendAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            AppendVisibilityEventSql,
            new
            {
                VisibilityEventIdentifier = _identifierFactory.Create(),
                GuestOrderIdentifier = guestOrderIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private sealed record OrderLockRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid OwnerPersonIdentifier,
        DateTime? SittingClosedAt);
}
