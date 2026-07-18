using MyRestaurant.Domain.Orders;
using Xunit;
using static MyRestaurant.Domain.Tests.OrderTestBuilders;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Exercises the pure fold from an order's event log to its current projection (TECHNICAL_SPECIFICATION
/// §8.5): the same line set, current prices, and fulfillment flags the SQL views produce. Events are
/// folded by sequence number regardless of input order, and removed lines drop out of the projection.
/// </summary>
public sealed class OrderProjectionTests
{
    [Fact]
    public void FromEvents_EmptyLog_IsAnEmptyOrder()
    {
        ProjectedOrder order = OrderProjection.FromEvents([]);

        Assert.Empty(order.Lines);
        Assert.Equal(0, order.PendingLineCount);
        Assert.Equal(0, order.FulfilledLineCount);
        Assert.Equal(0m, order.CurrentTotalAmount);
        Assert.Null(order.FirstSubmittedAt);
        Assert.Null(order.LastEventAt);
    }

    [Fact]
    public void FromEvents_SingleSubmission_ProjectsLinesAndTotal()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid lineA = Guid.NewGuid();
        Guid lineB = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0),
                Add(lineA, Guid.NewGuid(), 2, 9.50m),
                Add(lineB, Guid.NewGuid(), 1, 4.25m)),
        ]);

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(2, order.PendingLineCount);
        Assert.Equal(0, order.FulfilledLineCount);
        Assert.Equal(23.25m, order.CurrentTotalAmount); // 2*9.50 + 1*4.25
        Assert.Equal(At(0), order.FirstSubmittedAt);
        Assert.Equal(At(0), order.LastEventAt);
        Assert.Equal(orderId, order.GuestOrderIdentifier);
    }

    [Fact]
    public void FromEvents_PriceAdjustment_UsesTheLatestPrice()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid counter = Guid.NewGuid();
        Guid line = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0), Add(line, Guid.NewGuid(), 3, 10.00m)),
            PriceAdjustment(orderId, 2, counter, OrderActorRole.Counter, At(30), AdjustPrice(line, 8.00m, "loyalty discount")),
        ]);

        ProjectedOrderLine projected = Assert.Single(order.Lines);
        Assert.Equal(8.00m, projected.CurrentUnitPriceAmount);
        Assert.Equal(24.00m, projected.LineTotalAmount);
        Assert.Equal(24.00m, order.CurrentTotalAmount);
    }

    [Fact]
    public void FromEvents_RemovedLine_DropsOutOfTheProjection()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid keep = Guid.NewGuid();
        Guid drop = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0),
                Add(keep, Guid.NewGuid(), 1, 5.00m),
                Add(drop, Guid.NewGuid(), 1, 6.00m)),
            GuestSubmission(orderId, 2, guest, At(10), Remove(drop, "changed my mind")),
        ]);

        ProjectedOrderLine remaining = Assert.Single(order.Lines);
        Assert.Equal(keep, remaining.OrderLineIdentifier);
        Assert.Equal(5.00m, order.CurrentTotalAmount);
    }

    [Fact]
    public void FromEvents_Fulfillment_SetsFlagButStillCountsInTotal()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid kitchen = Guid.NewGuid();
        Guid line = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0), Add(line, Guid.NewGuid(), 2, 7.00m)),
            Fulfillment(orderId, 2, kitchen, OrderActorRole.Kitchen, At(60), Fulfill(line)),
        ]);

        ProjectedOrderLine projected = Assert.Single(order.Lines);
        Assert.True(projected.IsFulfilled);
        Assert.Equal(0, order.PendingLineCount);
        Assert.Equal(1, order.FulfilledLineCount);
        Assert.Equal(14.00m, order.CurrentTotalAmount);
    }

    [Fact]
    public void FromEvents_FulfillmentThenReversal_ReturnsLineToPending()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid kitchen = Guid.NewGuid();
        Guid line = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0), Add(line, Guid.NewGuid(), 1, 7.00m)),
            Fulfillment(orderId, 2, kitchen, OrderActorRole.Kitchen, At(60), Fulfill(line)),
            FulfillmentReversal(orderId, 3, kitchen, OrderActorRole.Kitchen, At(90), Revert(line)),
        ]);

        ProjectedOrderLine projected = Assert.Single(order.Lines);
        Assert.False(projected.IsFulfilled);
        Assert.Equal(1, order.PendingLineCount);
    }

    [Fact]
    public void FromEvents_FoldsBySequenceNumberNotInputOrder()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid counter = Guid.NewGuid();
        Guid line = Guid.NewGuid();

        OrderEvent add = GuestSubmission(orderId, 1, guest, At(0), Add(line, Guid.NewGuid(), 1, 10.00m));
        OrderEvent adjustToEleven = PriceAdjustment(orderId, 2, counter, OrderActorRole.Counter, At(20), AdjustPrice(line, 11.00m, "step one"));
        OrderEvent adjustToTwelve = PriceAdjustment(orderId, 3, counter, OrderActorRole.Counter, At(40), AdjustPrice(line, 12.00m, "step two"));

        // Deliberately shuffled input; the fold must apply seq 2 before seq 3, so 12.00 wins.
        ProjectedOrder order = OrderProjection.FromEvents([adjustToTwelve, add, adjustToEleven]);

        Assert.Equal(12.00m, Assert.Single(order.Lines).CurrentUnitPriceAmount);
    }

    [Fact]
    public void FromEvents_OrdersLinesByAddedTimeThenIdentifier()
    {
        Guid orderId = Guid.NewGuid();
        Guid guest = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        ProjectedOrder order = OrderProjection.FromEvents(
        [
            GuestSubmission(orderId, 1, guest, At(0), Add(first, Guid.NewGuid(), 1, 5.00m)),
            GuestSubmission(orderId, 2, guest, At(30), Add(second, Guid.NewGuid(), 1, 5.00m)),
        ]);

        Assert.Equal(first, order.Lines[0].OrderLineIdentifier);
        Assert.Equal(second, order.Lines[1].OrderLineIdentifier);
        Assert.Equal(At(0), order.FirstSubmittedAt);
        Assert.Equal(At(30), order.LastEventAt);
    }
}
