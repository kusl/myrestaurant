using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Orders;

public enum AppendOrderEventOutcome
{
    Appended,
    SittingNotFound,
    OrderNotFound,
    Rejected,
}

public sealed record AppendOrderEventResult(
    AppendOrderEventOutcome Outcome,
    Guid? SittingIdentifier,
    Guid? GuestOrderIdentifier,
    Guid? OrderEventIdentifier,
    long? SequenceNumber,
    OrderEventType? EventType,
    int LinesAdded,
    int LinesRemoved,
    int LinesFulfilled,
    int LinesFulfillmentReverted,
    bool KitchenNotificationWritten,
    IReadOnlyList<OrderMutationError> Errors,
    ProjectedOrder? Projection)
{
    public bool IsAppended => Outcome is AppendOrderEventOutcome.Appended;
}

public interface IOrderMutations
{
    Task<AppendOrderEventResult> AppendToLivingOrderAsync(
        Guid sittingIdentifier,
        Guid orderOwnerPersonIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default);

    Task<AppendOrderEventResult> AppendToOrderAsync(
        Guid guestOrderIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default);
}

public sealed class DapperOrderMutations : IOrderMutations
{
    private const string LockSittingSql = """
        SELECT table_sitting.table_sitting_identifier AS SittingIdentifier,
               table_sitting.closed_at                AS ClosedAt
        FROM table_sitting
        WHERE table_sitting.table_sitting_identifier = @SittingIdentifier
        FOR SHARE;
        """;

    private const string ReadOrderSql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS PersonIdentifier
        FROM guest_order
        WHERE guest_order.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private const string CreateOrderIfAbsentSql = """
        INSERT INTO guest_order (
            guest_order_identifier, table_sitting_identifier, person_identifier, created_at)
        VALUES (@GuestOrderIdentifier, @SittingIdentifier, @PersonIdentifier, @CreatedAt)
        ON CONFLICT (table_sitting_identifier, person_identifier) DO NOTHING;
        """;

    private const string LockOrderBySittingAndPersonSql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS PersonIdentifier
        FROM guest_order
        WHERE guest_order.table_sitting_identifier = @SittingIdentifier
          AND guest_order.person_identifier = @PersonIdentifier
        FOR UPDATE;
        """;

    private const string LockOrderByIdentifierSql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS PersonIdentifier
        FROM guest_order
        WHERE guest_order.guest_order_identifier = @GuestOrderIdentifier
        FOR UPDATE;
        """;

    private const string NextSequenceNumberSql = """
        SELECT coalesce(max(order_event.sequence_number), 0) + 1
        FROM order_event
        WHERE order_event.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private const string MembershipExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM table_sitting_member
            WHERE table_sitting_member.table_sitting_identifier = @SittingIdentifier
              AND table_sitting_member.person_identifier = @PersonIdentifier);
        """;

    private const string MenuItemsSql = """
        SELECT menu_item.menu_item_identifier AS MenuItemIdentifier,
               menu_item.price_amount         AS PriceAmount,
               menu_item.is_active            AS IsActive
        FROM menu_item
        WHERE menu_item.menu_item_identifier = ANY(@Identifiers);
        """;

    private const string InsertEventSql = """
        INSERT INTO order_event (
            order_event_identifier, guest_order_identifier, sequence_number,
            event_type, actor_person_identifier, actor_role, occurred_at)
        VALUES (
            @OrderEventIdentifier, @GuestOrderIdentifier, @SequenceNumber,
            @EventType, @ActorPersonIdentifier, @ActorRole, @OccurredAt);
        """;

    private const string InsertLineAddedSql = """
        INSERT INTO order_operation_line_added (
            order_operation_line_added_identifier, order_event_identifier, event_type,
            order_line_identifier, menu_item_identifier, quantity, unit_price_amount, customization_note)
        VALUES (
            @OperationIdentifier, @OrderEventIdentifier, @EventType,
            @OrderLineIdentifier, @MenuItemIdentifier, @Quantity, @UnitPriceAmount, @CustomizationNote);
        """;

    private const string InsertLineRemovedSql = """
        INSERT INTO order_operation_line_removed (
            order_operation_line_removed_identifier, order_event_identifier, event_type,
            order_line_identifier, reason)
        VALUES (
            @OperationIdentifier, @OrderEventIdentifier, @EventType,
            @OrderLineIdentifier, @Reason);
        """;

    private const string InsertLinePriceAdjustedSql = """
        INSERT INTO order_operation_line_price_adjusted (
            order_operation_line_price_adjusted_identifier, order_event_identifier, event_type,
            order_line_identifier, new_unit_price_amount, reason)
        VALUES (
            @OperationIdentifier, @OrderEventIdentifier, @EventType,
            @OrderLineIdentifier, @NewUnitPriceAmount, @Reason);
        """;

    private const string InsertLineFulfilledSql = """
        INSERT INTO order_operation_line_fulfilled (
            order_operation_line_fulfilled_identifier, order_event_identifier, event_type,
            order_line_identifier)
        VALUES (
            @OperationIdentifier, @OrderEventIdentifier, @EventType, @OrderLineIdentifier);
        """;

    private const string InsertLineFulfillmentRevertedSql = """
        INSERT INTO order_operation_line_fulfillment_reverted (
            order_operation_line_fulfillment_reverted_identifier, order_event_identifier, event_type,
            order_line_identifier)
        VALUES (
            @OperationIdentifier, @OrderEventIdentifier, @EventType, @OrderLineIdentifier);
        """;

    private const string InsertKitchenNotificationSql = """
        INSERT INTO kitchen_notification (
            kitchen_notification_identifier, order_event_identifier, event_type, kind, created_at)
        VALUES (
            @KitchenNotificationIdentifier, @OrderEventIdentifier, @EventType, @Kind, @CreatedAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperOrderMutations(
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

    public async Task<AppendOrderEventResult> AppendToLivingOrderAsync(
        Guid sittingIdentifier,
        Guid orderOwnerPersonIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SittingLockRow? sitting = await connection.QuerySingleOrDefaultAsync<SittingLockRow>(new CommandDefinition(
            LockSittingSql,
            new { SittingIdentifier = sittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (sitting is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NothingHappened(AppendOrderEventOutcome.SittingNotFound, null, null, proposed.EventType);
        }

        if (sitting.ClosedAt is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                CreateOrderIfAbsentSql,
                new
                {
                    GuestOrderIdentifier = _identifierFactory.Create(),
                    SittingIdentifier = sittingIdentifier,
                    PersonIdentifier = orderOwnerPersonIdentifier,
                    CreatedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        GuestOrderLockRow? order = await connection.QuerySingleOrDefaultAsync<GuestOrderLockRow>(new CommandDefinition(
            LockOrderBySittingAndPersonSql,
            new { SittingIdentifier = sittingIdentifier, PersonIdentifier = orderOwnerPersonIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NothingHappened(
                AppendOrderEventOutcome.OrderNotFound, sitting.SittingIdentifier, null, proposed.EventType);
        }

        return await CompleteAsync(connection, transaction, sitting, order, proposed, now, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AppendOrderEventResult> AppendToOrderAsync(
        Guid guestOrderIdentifier,
        ProposedOrderEvent proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        GuestOrderLockRow? located = await connection.QuerySingleOrDefaultAsync<GuestOrderLockRow>(new CommandDefinition(
            ReadOrderSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (located is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NothingHappened(AppendOrderEventOutcome.OrderNotFound, null, null, proposed.EventType);
        }

        SittingLockRow? sitting = await connection.QuerySingleOrDefaultAsync<SittingLockRow>(new CommandDefinition(
            LockSittingSql,
            new { SittingIdentifier = located.SittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (sitting is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NothingHappened(AppendOrderEventOutcome.SittingNotFound, null, guestOrderIdentifier, proposed.EventType);
        }

        GuestOrderLockRow? order = await connection.QuerySingleOrDefaultAsync<GuestOrderLockRow>(new CommandDefinition(
            LockOrderByIdentifierSql,
            new { GuestOrderIdentifier = guestOrderIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return NothingHappened(
                AppendOrderEventOutcome.OrderNotFound, sitting.SittingIdentifier, guestOrderIdentifier, proposed.EventType);
        }

        return await CompleteAsync(connection, transaction, sitting, order, proposed, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AppendOrderEventResult> CompleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        SittingLockRow sitting,
        GuestOrderLockRow order,
        ProposedOrderEvent proposed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long sequenceNumber = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            NextSequenceNumberSql,
            new { GuestOrderIdentifier = order.GuestOrderIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        IReadOnlyList<OrderEvent> priorEvents = await OrderEventReader
            .ReadAsync(connection, transaction, order.GuestOrderIdentifier, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<Guid, MenuItemPricing> menuItems = await ReadMenuItemsAsync(
            connection, transaction, proposed.Operations, cancellationToken).ConfigureAwait(false);

        ProposedOrderEvent effective = proposed with
        {
            Operations = ApplyServerSideValues(proposed.Operations, menuItems),
        };

        bool actorIsSittingMember = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            MembershipExistsSql,
            new
            {
                SittingIdentifier = sitting.SittingIdentifier,
                PersonIdentifier = effective.ActorPersonIdentifier,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        OrderMutationContext context = new(
            SittingIsOpen: sitting.ClosedAt is null,
            ActorIsOrderOwner: order.PersonIdentifier == effective.ActorPersonIdentifier,
            ActorIsSittingMember: actorIsSittingMember,
            MenuItems: menuItems.ToDictionary(
                pair => pair.Key,
                pair => new MenuItemSnapshot(pair.Key, pair.Value.IsActive)));

        OrderMutationValidationResult validation =
            OrderMutationValidator.Validate(priorEvents, effective, context);

        if (!validation.IsValid)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new AppendOrderEventResult(
                AppendOrderEventOutcome.Rejected,
                sitting.SittingIdentifier,
                order.GuestOrderIdentifier,
                OrderEventIdentifier: null,
                SequenceNumber: null,
                effective.EventType,
                LinesAdded: 0,
                LinesRemoved: 0,
                LinesFulfilled: 0,
                LinesFulfillmentReverted: 0,
                KitchenNotificationWritten: false,
                validation.Errors,
                OrderProjection.FromEvents(priorEvents));
        }

        Guid orderEventIdentifier = _identifierFactory.Create();
        string eventTypeName = OrderEventVocabulary.ToDatabase(effective.EventType);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertEventSql,
            new
            {
                OrderEventIdentifier = orderEventIdentifier,
                GuestOrderIdentifier = order.GuestOrderIdentifier,
                SequenceNumber = sequenceNumber,
                EventType = eventTypeName,
                ActorPersonIdentifier = effective.ActorPersonIdentifier,
                ActorRole = OrderEventVocabulary.ToDatabase(effective.ActorRole),
                OccurredAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (OrderOperation operation in effective.Operations)
        {
            await InsertOperationAsync(
                connection, transaction, orderEventIdentifier, eventTypeName, operation, cancellationToken)
                .ConfigureAwait(false);
        }

        bool kitchenNotificationWritten = ShouldNotifyKitchen(effective);
        if (kitchenNotificationWritten)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertKitchenNotificationSql,
                new
                {
                    KitchenNotificationIdentifier = _identifierFactory.Create(),
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    Kind = OrderEventVocabulary.InitialNotification,
                    CreatedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        OrderEvent applied = new(
            orderEventIdentifier,
            order.GuestOrderIdentifier,
            sequenceNumber,
            effective.EventType,
            effective.ActorPersonIdentifier,
            effective.ActorRole,
            now,
            effective.Operations);

        List<OrderEvent> committedLog = [.. priorEvents, applied];

        return new AppendOrderEventResult(
            AppendOrderEventOutcome.Appended,
            sitting.SittingIdentifier,
            order.GuestOrderIdentifier,
            orderEventIdentifier,
            sequenceNumber,
            effective.EventType,
            LinesAdded: effective.Operations.Count(operation => operation is LineAddedOperation),
            LinesRemoved: effective.Operations.Count(operation => operation is LineRemovedOperation),
            LinesFulfilled: effective.Operations.Count(operation => operation is LineFulfilledOperation),
            LinesFulfillmentReverted: effective.Operations.Count(operation => operation is LineFulfillmentRevertedOperation),
            kitchenNotificationWritten,
            [],
            OrderProjection.FromEvents(committedLog));
    }

    private Task InsertOperationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid orderEventIdentifier,
        string eventTypeName,
        OrderOperation operation,
        CancellationToken cancellationToken)
    {
        Guid operationIdentifier = _identifierFactory.Create();

        return operation switch
        {
            LineAddedOperation added => connection.ExecuteAsync(new CommandDefinition(
                InsertLineAddedSql,
                new
                {
                    OperationIdentifier = operationIdentifier,
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    added.OrderLineIdentifier,
                    added.MenuItemIdentifier,
                    added.Quantity,
                    added.UnitPriceAmount,
                    added.CustomizationNote,
                },
                transaction,
                cancellationToken: cancellationToken)),

            LineRemovedOperation removed => connection.ExecuteAsync(new CommandDefinition(
                InsertLineRemovedSql,
                new
                {
                    OperationIdentifier = operationIdentifier,
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    removed.OrderLineIdentifier,
                    removed.Reason,
                },
                transaction,
                cancellationToken: cancellationToken)),

            LinePriceAdjustedOperation adjusted => connection.ExecuteAsync(new CommandDefinition(
                InsertLinePriceAdjustedSql,
                new
                {
                    OperationIdentifier = operationIdentifier,
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    adjusted.OrderLineIdentifier,
                    adjusted.NewUnitPriceAmount,
                    adjusted.Reason,
                },
                transaction,
                cancellationToken: cancellationToken)),

            LineFulfilledOperation fulfilled => connection.ExecuteAsync(new CommandDefinition(
                InsertLineFulfilledSql,
                new
                {
                    OperationIdentifier = operationIdentifier,
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    fulfilled.OrderLineIdentifier,
                },
                transaction,
                cancellationToken: cancellationToken)),

            LineFulfillmentRevertedOperation reverted => connection.ExecuteAsync(new CommandDefinition(
                InsertLineFulfillmentRevertedSql,
                new
                {
                    OperationIdentifier = operationIdentifier,
                    OrderEventIdentifier = orderEventIdentifier,
                    EventType = eventTypeName,
                    reverted.OrderLineIdentifier,
                },
                transaction,
                cancellationToken: cancellationToken)),

            _ => throw new ArgumentOutOfRangeException(
                nameof(operation), operation.GetType().Name, "Unknown order operation type."),
        };
    }

    private static bool ShouldNotifyKitchen(ProposedOrderEvent proposed) => proposed.EventType switch
    {
        OrderEventType.GuestSubmission => true,
        OrderEventType.StaffEdit =>
            proposed.ActorRole is OrderActorRole.Counter or OrderActorRole.Administrator
            && proposed.Operations.Any(operation => operation is LineAddedOperation or LineRemovedOperation),
        _ => false,
    };

    private static async Task<IReadOnlyDictionary<Guid, MenuItemPricing>> ReadMenuItemsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<OrderOperation> operations,
        CancellationToken cancellationToken)
    {
        Guid[] identifiers = operations
            .OfType<LineAddedOperation>()
            .Select(added => added.MenuItemIdentifier)
            .Distinct()
            .ToArray();

        if (identifiers.Length == 0)
        {
            return new Dictionary<Guid, MenuItemPricing>();
        }

        IEnumerable<MenuItemPricing> rows = await connection.QueryAsync<MenuItemPricing>(new CommandDefinition(
            MenuItemsSql,
            new { Identifiers = identifiers },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(row => row.MenuItemIdentifier);
    }

    private static IReadOnlyList<OrderOperation> ApplyServerSideValues(
        IReadOnlyList<OrderOperation> operations,
        IReadOnlyDictionary<Guid, MenuItemPricing> menuItems)
    {
        List<OrderOperation> normalized = new(operations.Count);

        foreach (OrderOperation operation in operations)
        {
            normalized.Add(operation switch
            {
                LineAddedOperation added => added with
                {
                    UnitPriceAmount = menuItems.TryGetValue(added.MenuItemIdentifier, out MenuItemPricing? item)
                        ? item.PriceAmount
                        : added.UnitPriceAmount,
                    CustomizationNote = CollapseBlank(added.CustomizationNote),
                },
                LineRemovedOperation removed => removed with { Reason = CollapseBlank(removed.Reason) },
                LinePriceAdjustedOperation adjusted => adjusted with
                {
                    Reason = string.IsNullOrWhiteSpace(adjusted.Reason) ? adjusted.Reason : adjusted.Reason.Trim(),
                },
                _ => operation,
            });
        }

        return normalized;
    }

    private static string? CollapseBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AppendOrderEventResult NothingHappened(
        AppendOrderEventOutcome outcome,
        Guid? sittingIdentifier,
        Guid? guestOrderIdentifier,
        OrderEventType eventType)
        => new(
            outcome,
            sittingIdentifier,
            guestOrderIdentifier,
            OrderEventIdentifier: null,
            SequenceNumber: null,
            eventType,
            LinesAdded: 0,
            LinesRemoved: 0,
            LinesFulfilled: 0,
            LinesFulfillmentReverted: 0,
            KitchenNotificationWritten: false,
            [],
            Projection: null);

    private sealed record MenuItemPricing(Guid MenuItemIdentifier, decimal PriceAmount, bool IsActive);

    private sealed record SittingLockRow(Guid SittingIdentifier, DateTime? ClosedAt);

    private sealed record GuestOrderLockRow(Guid GuestOrderIdentifier, Guid SittingIdentifier, Guid PersonIdentifier);
}
