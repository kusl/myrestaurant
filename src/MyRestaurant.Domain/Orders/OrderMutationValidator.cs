namespace MyRestaurant.Domain.Orders;

/// <summary>What the validator needs to know about a menu item to accept a line-add (§6.5.4).</summary>
public sealed record MenuItemSnapshot(Guid MenuItemIdentifier, bool IsActive);

/// <summary>
/// The contextual facts a mutation is validated against — supplied by the data layer under the
/// serialized transaction (TECHNICAL_SPECIFICATION §6.6): sitting openness, whether the actor
/// owns this order and is a sitting member, and the current menu snapshot (existence + active
/// flag, re-read in the transaction).
/// </summary>
public sealed record OrderMutationContext(
    bool SittingIsOpen,
    bool ActorIsOrderOwner,
    bool ActorIsSittingMember,
    IReadOnlyDictionary<Guid, MenuItemSnapshot> MenuItems);

/// <summary>A proposed event awaiting validation before it is assigned a sequence number and written.</summary>
public sealed record ProposedOrderEvent(
    OrderEventType EventType,
    Guid ActorPersonIdentifier,
    OrderActorRole ActorRole,
    IReadOnlyList<OrderOperation> Operations);

/// <summary>One validation failure. <see cref="OperationIndex"/> is <c>-1</c> for event-level failures.</summary>
public sealed record OrderMutationError(int OperationIndex, string Reason);

/// <summary>The all-or-nothing outcome: valid, or invalid with per-operation reasons (§6.5.9).</summary>
public sealed record OrderMutationValidationResult(bool IsValid, IReadOnlyList<OrderMutationError> Errors)
{
    public static OrderMutationValidationResult Success { get; } = new(true, []);
}

/// <summary>
/// Enforces the §6.5 validation invariants (and the §6.3/§6.4 capability rules) as a pure
/// function of the order's prior events, a proposed event, and the transaction context. This is
/// what the order-mutating transaction evaluates under the lock (§6.6); on any failure the whole
/// event is rejected and the caller returns per-operation reasons plus a fresh projection so the
/// client restages (§6.5.9). The database CHECK/UNIQUE/FK constraints are the backstop; this is
/// the first line and the one that produces friendly reasons.
/// </summary>
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

        // (1) Every event owns at least one operation.
        if (proposed.Operations.Count == 0)
        {
            errors.Add(new OrderMutationError(EventLevel, "An event must contain at least one operation."));
        }

        // Event-type ↔ role consistency (mirrors the §8.2 same-row CHECKs).
        if (!OrderEventRules.RoleMayAuthor(proposed.EventType, proposed.ActorRole))
        {
            errors.Add(new OrderMutationError(EventLevel, $"A {proposed.ActorRole} actor may not author a {proposed.EventType} event."));
        }

        // (8) Post-close: administrators only; corrective event types only; never a guest submission.
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

        // (4) A guest submission requires an owner who is a member of the (open) sitting.
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

            // (§6.3) The operation subtype must be permitted for this event type.
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
        // (2 / DB UNIQUE) The line identifier is the line's identity — it must be new.
        if (priorStates.ContainsKey(added.OrderLineIdentifier) || !addedThisEvent.Add(added.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "The line identifier is already in use; a new line needs a new identifier."));
        }

        // (4) Quantity 1–100.
        if (added.Quantity is < MinimumQuantity or > MaximumQuantity)
        {
            errors.Add(new OrderMutationError(index, $"Quantity must be between {MinimumQuantity} and {MaximumQuantity}."));
        }

        // (4) The menu item must exist and be active, re-checked in this transaction.
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
        // (5) A removal may not reference a line added in the same event.
        if (addedThisEvent.Contains(removed.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "A line added in the same batch cannot also be removed in it."));
            return;
        }

        // (2) The referenced line must belong to this order.
        if (!priorStates.TryGetValue(removed.OrderLineIdentifier, out LineState? line))
        {
            errors.Add(new OrderMutationError(index, "The referenced line does not belong to this order."));
            return;
        }

        // (3 / DB UNIQUE) Removal is terminal — a line may not be removed twice.
        if (line.IsRemoved || !removedThisEvent.Add(removed.OrderLineIdentifier))
        {
            errors.Add(new OrderMutationError(index, "The line has already been removed."));
            return;
        }

        // (3) A guest may remove only their own, still-pending lines.
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

        // (7) Price adjustment targets non-removed lines and requires a non-empty reason.
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

        // (6) Fulfillment targets a currently-pending, non-removed line.
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

        // (6) A reversal targets a currently-fulfilled line (fulfilled/reverted must alternate).
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
