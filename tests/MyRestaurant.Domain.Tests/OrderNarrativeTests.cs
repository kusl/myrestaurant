using MyRestaurant.Domain.Orders;
using Xunit;
using static MyRestaurant.Domain.Tests.OrderTestBuilders;

namespace MyRestaurant.Domain.Tests;

public sealed class OrderNarrativeTests
{
    private static readonly Guid Order = Guid.Parse("0192f100-0000-7000-8000-0000000000a1");
    private static readonly Guid Guest = Guid.Parse("0192f100-0000-7000-8000-0000000000a2");
    private static readonly Guid OtherGuest = Guid.Parse("0192f100-0000-7000-8000-0000000000a3");
    private static readonly Guid Staff = Guid.Parse("0192f100-0000-7000-8000-0000000000a4");
    private static readonly Guid Soup = Guid.Parse("0192f100-0000-7000-8000-0000000000b1");
    private static readonly Guid Salad = Guid.Parse("0192f100-0000-7000-8000-0000000000b2");
    private static readonly Guid LineOne = Guid.Parse("0192f100-0000-7000-8000-0000000000c1");
    private static readonly Guid LineTwo = Guid.Parse("0192f100-0000-7000-8000-0000000000c2");

    [Fact]
    public void AnEmptyLog_YieldsNoLines()
        => Assert.Empty(OrderNarrative.FromEvents([]));

    [Fact]
    public void AGuestSubmission_YieldsAPendingLineCarryingItsCapturedPriceAndNote()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 2, 4.50m, "extra hot")),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.Equal(LineOne, line.OrderLineIdentifier);
        Assert.Equal(Soup, line.MenuItemIdentifier);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(4.50m, line.OriginalUnitPriceAmount);
        Assert.Equal(4.50m, line.CurrentUnitPriceAmount);
        Assert.Equal("extra hot", line.CustomizationNote);
        Assert.Equal(9.00m, line.LineTotalAmount);

        Assert.True(line.IsPending);
        Assert.False(line.IsFulfilled);
        Assert.False(line.IsRemoved);
        Assert.False(line.IsPriceAdjusted);

        Assert.Equal(OrderEventType.GuestSubmission, line.AddedByEventType);
        Assert.Equal(Guest, line.AddedByActorPersonIdentifier);
        Assert.Equal(OrderActorRole.Guest, line.AddedByActorRole);
        Assert.Equal(At(0), line.AddedAt);
    }

    [Fact]
    public void ARemovedLine_StaysInTheNarrativeWithItsActorAndReason_AndBillsNothing()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            StaffEdit(Order, 2, Staff, OrderActorRole.Counter, At(60), Remove(LineOne, "comped — long wait")),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.True(line.IsRemoved);
        Assert.False(line.IsPending);
        Assert.Equal(Staff, line.RemovedByActorPersonIdentifier);
        Assert.Equal(OrderActorRole.Counter, line.RemovedByActorRole);
        Assert.Equal("comped — long wait", line.RemovalReason);
        Assert.Equal(At(60), line.RemovedAt);

        Assert.Equal(0m, line.LineTotalAmount);
    }

    [Fact]
    public void ARemovalWithoutAReason_LeavesTheReasonNullRatherThanInventingOne()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            GuestSubmission(Order, 2, Guest, At(30), Remove(LineOne)),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.True(line.IsRemoved);
        Assert.Null(line.RemovalReason);
        Assert.Equal(OrderActorRole.Guest, line.RemovedByActorRole);
    }

    [Fact]
    public void PriceAdjustments_RecordOldToNewWithReason_InTheOrderTheyHappened()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            PriceAdjustment(Order, 2, Staff, OrderActorRole.Counter, At(60), AdjustPrice(LineOne, 3.00m, "half portion")),
            PriceAdjustment(Order, 3, Staff, OrderActorRole.Administrator, At(120), AdjustPrice(LineOne, 0m, "comped")),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.True(line.IsPriceAdjusted);
        Assert.Equal(2, line.PriceAdjustments.Count);

        Assert.Equal(4.50m, line.PriceAdjustments[0].PreviousUnitPriceAmount);
        Assert.Equal(3.00m, line.PriceAdjustments[0].NewUnitPriceAmount);
        Assert.Equal("half portion", line.PriceAdjustments[0].Reason);
        Assert.Equal(OrderActorRole.Counter, line.PriceAdjustments[0].ActorRole);

        Assert.Equal(3.00m, line.PriceAdjustments[1].PreviousUnitPriceAmount);
        Assert.Equal(0m, line.PriceAdjustments[1].NewUnitPriceAmount);
        Assert.Equal(OrderActorRole.Administrator, line.PriceAdjustments[1].ActorRole);

        Assert.Equal(4.50m, line.OriginalUnitPriceAmount);
        Assert.Equal(0m, line.CurrentUnitPriceAmount);
    }

    [Fact]
    public void FulfillmentAndReversal_Alternate_AndTheLatestBySequenceWins()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            Fulfillment(Order, 2, Staff, OrderActorRole.Kitchen, At(60), Fulfill(LineOne)),
            FulfillmentReversal(Order, 3, Staff, OrderActorRole.Kitchen, At(90), Revert(LineOne)),
            Fulfillment(Order, 4, Staff, OrderActorRole.Kitchen, At(120), Fulfill(LineOne)),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.True(line.IsFulfilled);
        Assert.False(line.IsPending);
    }

    [Fact]
    public void EventsAreFoldedBySequenceNumber_NotByTheOrderTheyArriveIn()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            Fulfillment(Order, 3, Staff, OrderActorRole.Kitchen, At(120), Fulfill(LineOne)),
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            FulfillmentReversal(Order, 2, Staff, OrderActorRole.Kitchen, At(60), Revert(LineOne)),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.True(line.IsFulfilled);
    }

    [Fact]
    public void OperationsTargetingAnUnknownLine_AreIgnoredRatherThanInventingALine()
    {
        Guid stranger = Guid.Parse("0192f100-0000-7000-8000-0000000000ff");

        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            Fulfillment(Order, 2, Staff, OrderActorRole.Kitchen, At(60), Fulfill(stranger)),
        ]);

        NarratedOrderLine line = Assert.Single(lines);
        Assert.Equal(LineOne, line.OrderLineIdentifier);
        Assert.False(line.IsFulfilled);
    }

    [Fact]
    public void LinesAreOrderedByWhenTheyWereAdded_ThenByIdentifier()
    {
        IReadOnlyList<NarratedOrderLine> lines = OrderNarrative.FromEvents([
            GuestSubmission(Order, 1, Guest, At(60), Add(LineTwo, Salad, 1, 6.00m)),
            GuestSubmission(Order, 2, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
        ]);

        Assert.Equal(
            new[] { LineOne, LineTwo },
            lines.Select(line => line.OrderLineIdentifier).ToArray());
    }

    [Fact]
    public void GuestMayRemove_TheirOwnPendingGuestSubmittedLine()
    {
        NarratedOrderLine line = Single(
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)));

        Assert.True(line.GuestMayRemove(Guest));
    }

    [Fact]
    public void GuestMayNotRemove_ALineSomebodyElseAdded()
    {
        NarratedOrderLine line = Single(
            GuestSubmission(Order, 1, OtherGuest, At(0), Add(LineOne, Soup, 1, 4.50m)));

        Assert.False(line.GuestMayRemove(Guest));
    }

    [Fact]
    public void GuestMayNotRemove_ALineStaffAddedForThem()
    {
        NarratedOrderLine line = Single(
            StaffEdit(Order, 1, Guest, OrderActorRole.Counter, At(0), Add(LineOne, Soup, 1, 4.50m)));

        Assert.False(line.GuestMayRemove(Guest));
    }

    [Fact]
    public void GuestMayNotRemove_AFulfilledLine()
    {
        NarratedOrderLine line = Single(
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            Fulfillment(Order, 2, Staff, OrderActorRole.Kitchen, At(60), Fulfill(LineOne)));

        Assert.False(line.GuestMayRemove(Guest));
    }

    [Fact]
    public void GuestMayNotRemove_AnAlreadyRemovedLine()
    {
        NarratedOrderLine line = Single(
            GuestSubmission(Order, 1, Guest, At(0), Add(LineOne, Soup, 1, 4.50m)),
            GuestSubmission(Order, 2, Guest, At(30), Remove(LineOne)));

        Assert.False(line.GuestMayRemove(Guest));
    }

    [Fact]
    public void NonRemovedLines_AgreeWithOrderProjectionOnARandomisedSequence()
    {
        IReadOnlyList<OrderEvent> events = GenerateSequence(seed: 20260726, eventCount: 120);

        IReadOnlyList<NarratedOrderLine> narrated = OrderNarrative.FromEvents(events);
        ProjectedOrder projected = OrderProjection.FromEvents(events);

        List<NarratedOrderLine> live = narrated.Where(line => !line.IsRemoved).ToList();

        Assert.Equal(projected.Lines.Count, live.Count);
        Assert.NotEmpty(live);

        for (int index = 0; index < live.Count; index++)
        {
            NarratedOrderLine mine = live[index];
            ProjectedOrderLine theirs = projected.Lines[index];

            Assert.Equal(theirs.OrderLineIdentifier, mine.OrderLineIdentifier);
            Assert.Equal(theirs.MenuItemIdentifier, mine.MenuItemIdentifier);
            Assert.Equal(theirs.Quantity, mine.Quantity);
            Assert.Equal(theirs.CurrentUnitPriceAmount, mine.CurrentUnitPriceAmount);
            Assert.Equal(theirs.CustomizationNote, mine.CustomizationNote);
            Assert.Equal(theirs.IsFulfilled, mine.IsFulfilled);
            Assert.Equal(theirs.AddedAt, mine.AddedAt);
            Assert.Equal(theirs.AddedByOrderEventIdentifier, mine.AddedByOrderEventIdentifier);
            Assert.Equal(theirs.LineTotalAmount, mine.LineTotalAmount);
        }

        Assert.Equal(projected.CurrentTotalAmount, live.Sum(line => line.LineTotalAmount));

        Assert.Contains(narrated, line => line.IsRemoved);
        Assert.Contains(narrated, line => line.IsFulfilled);
        Assert.Contains(narrated, line => line.IsPriceAdjusted);
    }

    private static NarratedOrderLine Single(params OrderEvent[] events)
        => Assert.Single(OrderNarrative.FromEvents(events));

    private static IReadOnlyList<OrderEvent> GenerateSequence(int seed, int eventCount)
    {
        Random random = new(seed);
        Guid[] menuItems = [Soup, Salad, Guid.Parse("0192f100-0000-7000-8000-0000000000b3")];

        List<OrderEvent> events = [];
        List<Guid> pending = [];
        List<Guid> fulfilled = [];
        List<Guid> live = [];

        Guid prologueRemoved = Guid.NewGuid();
        Guid prologueFulfilled = Guid.NewGuid();
        Guid prologueAdjusted = Guid.NewGuid();

        events.Add(GuestSubmission(
            Order,
            1,
            Guest,
            At(1),
            Add(prologueRemoved, Soup, 1, 4.50m),
            Add(prologueFulfilled, Salad, 2, 6.00m, "no dressing"),
            Add(prologueAdjusted, Soup, 3, 4.50m)));

        events.Add(Fulfillment(Order, 2, Staff, OrderActorRole.Kitchen, At(2), Fulfill(prologueFulfilled)));
        events.Add(PriceAdjustment(Order, 3, Staff, OrderActorRole.Counter, At(3), AdjustPrice(prologueAdjusted, 3.25m, "half portion")));
        events.Add(StaffEdit(Order, 4, Staff, OrderActorRole.Counter, At(4), Remove(prologueRemoved, "sent back")));

        live.Add(prologueFulfilled);
        live.Add(prologueAdjusted);
        fulfilled.Add(prologueFulfilled);
        pending.Add(prologueAdjusted);

        for (int sequence = events.Count + 1; sequence <= eventCount; sequence++)
        {
            DateTimeOffset occurredAt = At(sequence * 17);
            int choice = random.Next(0, 100);

            if (choice < 40 || live.Count == 0)
            {
                int adds = random.Next(1, 4);
                List<OrderOperation> operations = [];

                for (int add = 0; add < adds; add++)
                {
                    Guid lineIdentifier = Guid.NewGuid();
                    operations.Add(Add(
                        lineIdentifier,
                        menuItems[random.Next(menuItems.Length)],
                        random.Next(1, 5),
                        decimal.Round(random.Next(150, 2400) / 100m, 2),
                        random.Next(0, 3) == 0 ? $"note {sequence}" : null));

                    pending.Add(lineIdentifier);
                    live.Add(lineIdentifier);
                }

                events.Add(GuestSubmission(Order, sequence, Guest, occurredAt, [.. operations]));
                continue;
            }

            if (choice < 62 && pending.Count > 0)
            {
                Guid target = Take(random, pending);
                fulfilled.Add(target);
                events.Add(Fulfillment(Order, sequence, Staff, OrderActorRole.Kitchen, occurredAt, Fulfill(target)));
                continue;
            }

            if (choice < 74 && fulfilled.Count > 0)
            {
                Guid target = Take(random, fulfilled);
                pending.Add(target);
                events.Add(FulfillmentReversal(Order, sequence, Staff, OrderActorRole.Kitchen, occurredAt, Revert(target)));
                continue;
            }

            if (choice < 88 && live.Count > 0)
            {
                Guid target = live[random.Next(live.Count)];
                events.Add(PriceAdjustment(
                    Order,
                    sequence,
                    Staff,
                    OrderActorRole.Counter,
                    occurredAt,
                    AdjustPrice(target, decimal.Round(random.Next(0, 2400) / 100m, 2), $"adjusted at {sequence}")));
                continue;
            }

            Guid removed = Take(random, live);
            pending.Remove(removed);
            fulfilled.Remove(removed);
            events.Add(StaffEdit(
                Order,
                sequence,
                Staff,
                OrderActorRole.Counter,
                occurredAt,
                Remove(removed, random.Next(0, 2) == 0 ? $"removed at {sequence}" : null)));
        }

        return events;
    }

    private static Guid Take(Random random, List<Guid> candidates)
    {
        int index = random.Next(candidates.Count);
        Guid value = candidates[index];
        candidates.RemoveAt(index);
        return value;
    }
}
