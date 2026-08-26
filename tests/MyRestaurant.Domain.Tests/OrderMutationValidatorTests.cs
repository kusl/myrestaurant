using MyRestaurant.Domain.Orders;
using Xunit;
using static MyRestaurant.Domain.Tests.OrderTestBuilders;

namespace MyRestaurant.Domain.Tests;

public sealed class OrderMutationValidatorTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid Burger = Guid.NewGuid();
    private static readonly Guid Fries = Guid.NewGuid();
    private static readonly Guid Guest = Guid.NewGuid();
    private static readonly Guid OtherGuest = Guid.NewGuid();
    private static readonly Guid Counter = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Kitchen = Guid.NewGuid();

    [Fact]
    public void EmptyOperations_IsRejectedAtEventLevel()
    {
        ProposedOrderEvent proposed = new(OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest, []);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void RoleThatMayNotAuthorTheEventType_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Guest, OrderActorRole.Guest,
            [AdjustPrice(Guid.NewGuid(), 1.00m, "nope")]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void KitchenAuthoringGuestSubmission_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Kitchen, OrderActorRole.Kitchen,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void ClosedSitting_RejectsNonAdministratorEvents()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.Fulfillment, Kitchen, OrderActorRole.Kitchen, [Fulfill(line)]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate(
            WithGuestLine(line, Guest), proposed, ClosedContext());

        AssertInvalidAt(result, OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void ClosedSitting_RejectsGuestSubmissionEvenFromAdministrator()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Admin, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, ClosedContext()), OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void ClosedSitting_AllowsAdministratorCorrectiveEvent()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Admin, OrderActorRole.Administrator,
            [AdjustPrice(line, 8.00m, "post-close correction")]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate(
            WithGuestLine(line, Guest), proposed, ClosedContext());

        AssertValid(result);
    }

    [Fact]
    public void GuestSubmission_ByNonOwner_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m)]);

        AssertInvalidAt(
            OrderMutationValidator.Validate([], proposed, OpenContext(owner: false)),
            OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void GuestSubmission_ByNonMember_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m)]);

        AssertInvalidAt(
            OrderMutationValidator.Validate([], proposed, OpenContext(member: false)),
            OrderMutationValidator.EventLevel);
    }

    [Fact]
    public void GuestSubmission_ByOwningMember_IsAccepted()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, 2, 9.00m, "no pickles")]);

        AssertValid(OrderMutationValidator.Validate([], proposed, OpenContext()));
    }

    [Fact]
    public void OperationNotAllowedForEventType_IsRejectedPerOperation()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void LineAdd_WithDuplicateExistingIdentifier_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(line, Burger, 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()), 0);
    }

    [Fact]
    public void LineAdd_WithDuplicateIdentifierInSameEvent_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(line, Burger, 1, 9.00m), Add(line, Burger, 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void LineAdd_WithQuantityOutOfRange_IsRejected(int quantity)
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, quantity, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void LineAdd_ForUnknownMenuItem_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Guid.NewGuid(), 1, 9.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void LineAdd_ForInactiveMenuItem_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Fries, 1, 3.00m)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext(friesActive: false)), 0);
    }

    [Fact]
    public void LineRemoval_OfLineAddedInSameEvent_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(line, Burger, 1, 9.00m), Remove(line)]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 1);
    }

    [Fact]
    public void LineRemoval_OfUnknownLine_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.StaffEdit, Counter, OrderActorRole.Counter, [Remove(Guid.NewGuid())]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void LineRemoval_OfAlreadyRemovedLine_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.StaffEdit, Counter, OrderActorRole.Counter, [Remove(line)]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate(
            WithGuestLine(line, Guest, removed: true), proposed, OpenContext());

        AssertInvalidAt(result, 0);
    }

    [Fact]
    public void GuestRemovingAnotherGuestsLine_IsRejected()
    {
        Guid line = Guid.NewGuid();

        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest, [Remove(line)]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate(
            WithGuestLine(line, OtherGuest), proposed, OpenContext());

        AssertInvalidAt(result, 0);
    }

    [Fact]
    public void GuestRemovingOwnFulfilledLine_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest, [Remove(line)]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate(
            WithGuestLine(line, Guest, fulfilled: true), proposed, OpenContext());

        AssertInvalidAt(result, 0);
    }

    [Fact]
    public void GuestRemovingOwnPendingLine_IsAccepted()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest, [Remove(line)]);

        AssertValid(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()));
    }

    [Fact]
    public void StaffRemovingAnyLine_IsAccepted()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.StaffEdit, Counter, OrderActorRole.Counter, [Remove(line, "comped")]);

        AssertValid(OrderMutationValidator.Validate(WithGuestLine(line, OtherGuest), proposed, OpenContext()));
    }

    [Fact]
    public void PriceAdjustment_OfUnknownLine_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [AdjustPrice(Guid.NewGuid(), 5.00m, "discount")]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void PriceAdjustment_OfRemovedLine_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [AdjustPrice(line, 5.00m, "discount")]);

        AssertInvalidAt(
            OrderMutationValidator.Validate(WithGuestLine(line, Guest, removed: true), proposed, OpenContext()),
            0);
    }

    [Fact]
    public void PriceAdjustment_WithBlankReason_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [AdjustPrice(line, 5.00m, "   ")]);

        AssertInvalidAt(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()), 0);
    }

    [Fact]
    public void PriceAdjustment_WithNegativePrice_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [AdjustPrice(line, -1.00m, "typo")]);

        AssertInvalidAt(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()), 0);
    }

    [Fact]
    public void PriceAdjustment_WithReasonOnExistingLine_IsAccepted()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.PriceAdjustment, Counter, OrderActorRole.Counter,
            [AdjustPrice(line, 7.50m, "loyalty discount")]);

        AssertValid(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()));
    }

    [Fact]
    public void Fulfillment_OfUnknownLine_IsRejected()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.Fulfillment, Kitchen, OrderActorRole.Kitchen, [Fulfill(Guid.NewGuid())]);

        AssertInvalidAt(OrderMutationValidator.Validate([], proposed, OpenContext()), 0);
    }

    [Fact]
    public void Fulfillment_OfAlreadyFulfilledLine_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.Fulfillment, Kitchen, OrderActorRole.Kitchen, [Fulfill(line)]);

        AssertInvalidAt(
            OrderMutationValidator.Validate(WithGuestLine(line, Guest, fulfilled: true), proposed, OpenContext()),
            0);
    }

    [Fact]
    public void Fulfillment_OfPendingLine_IsAccepted()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.Fulfillment, Kitchen, OrderActorRole.Kitchen, [Fulfill(line)]);

        AssertValid(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()));
    }

    [Fact]
    public void Reversal_OfPendingLine_IsRejected()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.FulfillmentReversal, Kitchen, OrderActorRole.Kitchen, [Revert(line)]);

        AssertInvalidAt(OrderMutationValidator.Validate(WithGuestLine(line, Guest), proposed, OpenContext()), 0);
    }

    [Fact]
    public void Reversal_OfFulfilledLine_IsAccepted()
    {
        Guid line = Guid.NewGuid();
        ProposedOrderEvent proposed = new(
            OrderEventType.FulfillmentReversal, Kitchen, OrderActorRole.Kitchen, [Revert(line)]);

        AssertValid(
            OrderMutationValidator.Validate(WithGuestLine(line, Guest, fulfilled: true), proposed, OpenContext()));
    }

    [Fact]
    public void OneBadOperation_RejectsTheWholeEvent()
    {
        ProposedOrderEvent proposed = new(
            OrderEventType.GuestSubmission, Guest, OrderActorRole.Guest,
            [Add(Guid.NewGuid(), Burger, 1, 9.00m), Add(Guid.NewGuid(), Burger, 0, 9.00m)]);

        OrderMutationValidationResult result = OrderMutationValidator.Validate([], proposed, OpenContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.OperationIndex == 1);
    }

    private static IReadOnlyList<OrderEvent> WithGuestLine(Guid lineIdentifier, Guid addedBy, bool fulfilled = false, bool removed = false)
    {
        List<OrderEvent> events = [GuestSubmission(OrderId, 1, addedBy, At(0), Add(lineIdentifier, Burger, 1, 9.00m))];
        long sequence = 2;

        if (fulfilled)
        {
            events.Add(Fulfillment(OrderId, sequence++, Kitchen, OrderActorRole.Kitchen, At(10), Fulfill(lineIdentifier)));
        }

        if (removed)
        {
            events.Add(StaffEdit(OrderId, sequence, Counter, OrderActorRole.Counter, At(20), Remove(lineIdentifier)));
        }

        return events;
    }

    private static OrderMutationContext OpenContext(bool owner = true, bool member = true, bool friesActive = true)
        => new(SittingIsOpen: true, ActorIsOrderOwner: owner, ActorIsSittingMember: member, MenuItems: Menu(friesActive));

    private static OrderMutationContext ClosedContext(bool friesActive = true)
        => new(SittingIsOpen: false, ActorIsOrderOwner: true, ActorIsSittingMember: true, MenuItems: Menu(friesActive));

    private static IReadOnlyDictionary<Guid, MenuItemSnapshot> Menu(bool friesActive)
        => new Dictionary<Guid, MenuItemSnapshot>
        {
            [Burger] = new(Burger, true),
            [Fries] = new(Fries, friesActive),
        };

    private static void AssertValid(OrderMutationValidationResult result)
        => Assert.True(
            result.IsValid,
            "Expected valid, but got: " + string.Join("; ", result.Errors.Select(error => $"[{error.OperationIndex}] {error.Reason}")));

    private static void AssertInvalidAt(OrderMutationValidationResult result, int expectedOperationIndex)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.OperationIndex == expectedOperationIndex);
    }
}
