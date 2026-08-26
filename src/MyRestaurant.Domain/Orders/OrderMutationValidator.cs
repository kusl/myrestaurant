namespace MyRestaurant.Domain.Orders;

public sealed record MenuItemSnapshot(Guid MenuItemIdentifier, bool IsActive);

public sealed record OrderMutationContext(
    bool SittingIsOpen,
    bool ActorIsOrderOwner,
    bool ActorIsSittingMember,
    IReadOnlyDictionary<Guid, MenuItemSnapshot> MenuItems);

public sealed record ProposedOrderEvent(
    OrderEventType EventType,
    Guid ActorPersonIdentifier,
    OrderActorRole ActorRole,
    IReadOnlyList<OrderOperation> Operations);

public sealed record OrderMutationError(int OperationIndex, string Reason);

public sealed record OrderMutationValidationResult(bool IsValid, IReadOnlyList<OrderMutationError> Errors)
{
    public static OrderMutationValidationResult Success { get; } = new(true, []);
}

public static class OrderMutationValidator
{
    public const int EventLevel = -1;
    public const int MinimumQuantity = 1;
    public const int MaximumQuantity = 100;

    public static OrderMutationValidationResult Validate(
        IReadOnlyList<OrderEvent> priorEvents,
        ProposedOrderEvent proposed,
        OrderMutationContext context)
    {
        ArgumentNullException.ThrowIfNull(priorEvents);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(context);

        List<OrderMutationError> errors = [];

        if (proposed.Operations.Count == 0)
        {
            errors.Add(new OrderMutationError(EventLevel, "An event must contain at least one operation."));
        }

        if (!OrderEventRules.RoleMayAuthor(proposed.EventType, proposed.ActorRole))
        {
            errors.Add(new OrderMutationError(EventLevel, $"A {proposed.ActorRole} actor may not author a {proposed.EventType} event."));
        }

        if (!context.SittingIsOpen)
        {
            if (proposed.ActorRole != OrderActorRole.Administrator)
            {
                errors.Add(new OrderMutationError(EventLevel, "The sitting is closed; only an administrator may append corrective events."));
            }

            if (proposed.EventType == OrderEventType.GuestSubmission)
            {
                errors.Add(new OrderMutationError(EventLevel, "A closed sitting cannot receive a guest submission."));
            }
        }

        if (proposed.EventType == OrderEventType.GuestSubmission)
        {
            if (!context.ActorIsOrderOwner)
            {
                errors.Add(new OrderMutationError(EventLevel, "Only the order owner may submit to this order."));
            }

            if (!context.ActorIsSittingMember)
            {
                errors.Add(new OrderMutationError(EventLevel, "The actor is not a member of this sitting."));
            }
        }

        IReadOnlyDictionary<Guid, LineState> priorStates = OrderProjection.BuildLineStates(priorEvents);
        HashSet<Guid> addedThisEvent = [];
        HashSet<Guid> removedThisEvent = [];

        for (int index = 0; index < proposed.Operations.Count; index++)
        {
            OrderOperation operation = proposed.Operations[index];

            if (!OrderEventRules.OperationIsAllowedFor(proposed.EventType, operation))
            {
                errors.Add(new OrderMutationError(index, $"A {DescribeOperation(operation)} is not permitted in a {proposed.EventType} event."));
                continue;
            }

            switch (operation)
            {
                case LineAddedOperation added:
                    ValidateLineAdded(added, index, context, priorStates, addedThisEvent, errors);
                    break;

                case LineRemovedOperation removed:
                    ValidateLineRemoved(removed, index, proposed, priorStates, addedThisEvent, removedThisEvent, errors);
                    break;

                case LinePriceAdjustedOperation adjusted:
                    ValidateLinePriceAdjusted(adjusted, index, priorStates, errors);
                    break;

                case LineFulfilledOperation fulfilled:
                    ValidateLineFulfilled(fulfilled, index, priorStates, errors);
                    break;

                case LineFulfillmentRevertedOperation reverted:
                    ValidateLineFulfillmentReverted(reverted, index, priorStates, errors);
                    break;
            }
        }

        return errors.Count == 0 ? OrderMutationValidationResult.Success : new OrderMutationValidationResult(false, errors);
    }

    private static void ValidateLineAdded(
        LineAddedOperation added,
        int index,
        OrderMutationContext context,
        IReadOnlyDictionary<Guid, LineState> priorStates,
        HashSet<Guid> addedThisEvent,
        List<OrderMutationError> errors)
    {
        if (priorStates.ContainsKey(added.OrderLineIdentifier) || !addedThisEvent.Add(added.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "The line identifier is already in use; a new line needs a new identifier."));
        }

        if (added.Quantity is < MinimumQuantity or > MaximumQuantity)
        {
            errors.Add(new OrderMutationError(index, $"Quantity must be between {MinimumQuantity} and {MaximumQuantity}."));
        }

        if (!context.MenuItems.TryGetValue(added.MenuItemIdentifier, out MenuItemSnapshot? menuItem))
        {
            errors.Add(new OrderMutationError(index, "The menu item does not exist."));
        }
        else if (!menuItem.IsActive)
        {
            errors.Add(new OrderMutationError(index, "The menu item is currently unavailable."));
        }
    }

    private static void ValidateLineRemoved(
        LineRemovedOperation removed,
        int index,
        ProposedOrderEvent proposed,
        IReadOnlyDictionary<Guid, LineState> priorStates,
        HashSet<Guid> addedThisEvent,
        HashSet<Guid> removedThisEvent,
        List<OrderMutationError> errors)
    {
        if (addedThisEvent.Contains(removed.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "A line added in the same batch cannot also be removed in it."));
            return;
        }

        if (!priorStates.TryGetValue(removed.OrderLineIdentifier, out LineState? line))
        {
            errors.Add(new OrderMutationError(index, "The referenced line does not belong to this order."));
            return;
        }

        if (line.IsRemoved || !removedThisEvent.Add(removed.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "The line has already been removed."));
            return;
        }

        if (proposed.ActorRole == OrderActorRole.Guest)
        {
            bool addedByThisGuest = line.AddedByEventType == OrderEventType.GuestSubmission
                && line.AddedByActorPersonIdentifier == proposed.ActorPersonIdentifier;

            if (!addedByThisGuest)
            {
                errors.Add(new OrderMutationError(index, "A guest may remove only lines they added themselves."));
            }
            else if (line.IsFulfilled)
            {
                errors.Add(new OrderMutationError(index, "A fulfilled line cannot be removed by the guest."));
            }
        }
    }

    private static void ValidateLinePriceAdjusted(
        LinePriceAdjustedOperation adjusted,
        int index,
        IReadOnlyDictionary<Guid, LineState> priorStates,
        List<OrderMutationError> errors)
    {
        if (!priorStates.TryGetValue(adjusted.OrderLineIdentifier, out LineState? line))
        {
            errors.Add(new OrderMutationError(index, "The referenced line does not belong to this order."));
            return;
        }

        if (line.IsRemoved)
        {
            errors.Add(new OrderMutationError(index, "A removed line's price cannot be adjusted."));
        }

        if (string.IsNullOrWhiteSpace(adjusted.Reason))
        {
            errors.Add(new OrderMutationError(index, "A price adjustment requires a reason."));
        }

        if (adjusted.NewUnitPriceAmount < 0m)
        {
            errors.Add(new OrderMutationError(index, "The adjusted price must not be negative."));
        }
    }

    private static void ValidateLineFulfilled(
        LineFulfilledOperation fulfilled,
        int index,
        IReadOnlyDictionary<Guid, LineState> priorStates,
        List<OrderMutationError> errors)
    {
        if (!priorStates.TryGetValue(fulfilled.OrderLineIdentifier, out LineState? line))
        {
            errors.Add(new OrderMutationError(index, "The referenced line does not belong to this order."));
            return;
        }

        if (line.IsRemoved)
        {
            errors.Add(new OrderMutationError(index, "A removed line cannot be fulfilled."));
        }
        else if (line.IsFulfilled)
        {
            errors.Add(new OrderMutationError(index, "The line is already fulfilled."));
        }
    }

    private static void ValidateLineFulfillmentReverted(
        LineFulfillmentRevertedOperation reverted,
        int index,
        IReadOnlyDictionary<Guid, LineState> priorStates,
        List<OrderMutationError> errors)
    {
        if (!priorStates.TryGetValue(reverted.OrderLineIdentifier, out LineState? line))
        {
            errors.Add(new OrderMutationError(index, "The referenced line does not belong to this order."));
            return;
        }

        if (line.IsRemoved)
        {
            errors.Add(new OrderMutationError(index, "A removed line's fulfillment cannot be reverted."));
        }
        else if (!line.IsFulfilled)
        {
            errors.Add(new OrderMutationError(index, "The line is not fulfilled, so its fulfillment cannot be reverted."));
        }
    }

    private static string DescribeOperation(OrderOperation operation) => operation switch
    {
        LineAddedOperation => "line-add",
        LineRemovedOperation => "line-removal",
        LinePriceAdjustedOperation => "price-adjustment",
        LineFulfilledOperation => "fulfillment",
        LineFulfillmentRevertedOperation => "fulfillment-reversal",
        _ => "operation",
    };
}
