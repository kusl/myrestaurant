using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class OrderStagingTests
{
    private static readonly Guid SoupIdentifier = Guid.Parse("0192f200-0000-7000-8000-0000000000a1");
    private static readonly Guid SaladIdentifier = Guid.Parse("0192f200-0000-7000-8000-0000000000a2");
    private static readonly Guid LineOne = Guid.Parse("0192f200-0000-7000-8000-0000000000b1");
    private static readonly Guid LineTwo = Guid.Parse("0192f200-0000-7000-8000-0000000000b2");

    private static readonly MenuItemSummary Soup = Item(SoupIdentifier, "Soup", 4.50m, isActive: true);
    private static readonly MenuItemSummary Salad = Item(SaladIdentifier, "Salad", 6.00m, isActive: true);
    private static readonly MenuItemSummary Salmon = Item(Guid.Parse("0192f200-0000-7000-8000-0000000000a3"), "Salmon", 18.00m, isActive: false);

    [Fact]
    public void ANewStagingArea_IsEmptyAndHasNothingToSend()
    {
        OrderStaging staging = new();

        Assert.True(staging.IsEmpty);
        Assert.Equal(0, staging.OperationCount);
        Assert.Empty(staging.Lines);
        Assert.Empty(staging.Removals);
    }

    [Fact]
    public void Stage_KeepsTheItemWithATrimmedNote()
    {
        OrderStaging staging = new();

        StagingResult result = staging.Stage(Soup, 2, "   extra hot   ");

        Assert.True(result.Accepted);
        Assert.Null(result.Reason);

        StagedOrderLine line = Assert.Single(staging.Lines);
        Assert.Equal(SoupIdentifier, line.MenuItemIdentifier);
        Assert.Equal("Soup", line.MenuItemName);
        Assert.Equal(2, line.Quantity);
        Assert.Equal("extra hot", line.CustomizationNote);
        Assert.NotEqual(Guid.Empty, line.StagingIdentifier);

        Assert.False(staging.IsEmpty);
        Assert.Equal(1, staging.OperationCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Stage_CollapsesABlankNoteToNull_TheSameWayTheTransactionWould(string? note)
    {
        OrderStaging staging = new();

        staging.Stage(Soup, 1, note);

        Assert.Null(Assert.Single(staging.Lines).CustomizationNote);
    }

    [Fact]
    public void Stage_RefusesAnItemThatIsCurrentlyUnavailable()
    {
        OrderStaging staging = new();

        StagingResult result = staging.Stage(Salmon, 1, null);

        Assert.False(result.Accepted);
        Assert.Equal("Salmon is currently unavailable.", result.Reason);
        Assert.Empty(staging.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Stage_RefusesAQuantityOutsideOneToOneHundred(int quantity)
    {
        OrderStaging staging = new();

        StagingResult result = staging.Stage(Soup, quantity, null);

        Assert.False(result.Accepted);
        Assert.Equal("Choose a quantity between 1 and 100.", result.Reason);
        Assert.Empty(staging.Lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Stage_AcceptsBothEndsOfTheRange(int quantity)
    {
        OrderStaging staging = new();

        Assert.True(staging.Stage(Soup, quantity, null).Accepted);
        Assert.Equal(quantity, Assert.Single(staging.Lines).Quantity);
    }

    [Fact]
    public void TheQuantityBoundsAreTheValidatorsOwn_SoTheTwoCannotDrift()
    {
        Assert.Equal(OrderMutationValidator.MinimumQuantity, OrderStaging.MinimumQuantity);
        Assert.Equal(OrderMutationValidator.MaximumQuantity, OrderStaging.MaximumQuantity);
    }

    [Fact]
    public void StagingTheSameItemTwice_MakesTwoRows_NotOneDoubled()
    {
        OrderStaging staging = new();

        staging.Stage(Soup, 1, "no salt");
        staging.Stage(Soup, 1, "extra salt");

        Assert.Equal(2, staging.Lines.Count);
        Assert.Equal(2, staging.OperationCount);
    }

    [Fact]
    public void Unstage_TakesOutOnlyThatRow_AndReportsAnUnknownKey()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 1, null);
        staging.Stage(Salad, 1, null);

        Guid target = staging.Lines[0].StagingIdentifier;

        Assert.True(staging.Unstage(target));
        Assert.Equal(SaladIdentifier, Assert.Single(staging.Lines).MenuItemIdentifier);

        Assert.False(staging.Unstage(target));
    }

    [Fact]
    public void SetQuantity_ChangesTheRowInPlaceAndKeepsItsPosition()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 1, "keep me");
        staging.Stage(Salad, 1, null);

        Guid target = staging.Lines[0].StagingIdentifier;

        Assert.True(staging.SetQuantity(target, 7).Accepted);

        Assert.Equal(SoupIdentifier, staging.Lines[0].MenuItemIdentifier);
        Assert.Equal(7, staging.Lines[0].Quantity);
        Assert.Equal("keep me", staging.Lines[0].CustomizationNote);
        Assert.Equal(target, staging.Lines[0].StagingIdentifier);
    }

    [Fact]
    public void SetQuantity_RefusesAnOutOfRangeValueAndLeavesTheRowUntouched()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 3, null);
        Guid target = staging.Lines[0].StagingIdentifier;

        StagingResult result = staging.SetQuantity(target, 0);

        Assert.False(result.Accepted);
        Assert.Equal(3, staging.Lines[0].Quantity);
    }

    [Fact]
    public void SetQuantity_RefusesAnUnknownRow()
    {
        OrderStaging staging = new();

        StagingResult result = staging.SetQuantity(Guid.NewGuid(), 2);

        Assert.False(result.Accepted);
        Assert.Equal("That item is no longer in your basket.", result.Reason);
    }

    [Fact]
    public void MarkingALineForRemoval_IsIdempotent_AndUntickingTakesItBackOut()
    {
        OrderStaging staging = new();

        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);

        Assert.True(staging.IsMarkedForRemoval(LineOne));
        Assert.Single(staging.Removals);
        Assert.Equal(1, staging.OperationCount);

        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: false);

        Assert.False(staging.IsMarkedForRemoval(LineOne));
        Assert.Empty(staging.Removals);
        Assert.True(staging.IsEmpty);
    }

    [Fact]
    public void PruneRemovals_DropsMarksForLinesThatAreNoLongerTheGuestsToRemove()
    {
        OrderStaging staging = new();
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);
        staging.SetMarkedForRemoval(LineTwo, "1 × Salad", marked: true);

        int dropped = staging.PruneRemovals([LineOne]);

        Assert.Equal(1, dropped);
        Assert.True(staging.IsMarkedForRemoval(LineOne));
        Assert.False(staging.IsMarkedForRemoval(LineTwo));
    }

    [Fact]
    public void PruneRemovals_DropsNothingWhenEveryMarkIsStillGood()
    {
        OrderStaging staging = new();
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);

        Assert.Equal(0, staging.PruneRemovals([LineOne, LineTwo]));
        Assert.Single(staging.Removals);
    }

    [Fact]
    public void PruneRemovals_LeavesStagedAddsAlone()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 1, null);
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);

        staging.PruneRemovals([]);

        Assert.Single(staging.Lines);
        Assert.Empty(staging.Removals);
    }

    [Fact]
    public void Build_EmitsEveryAddThenEveryRemoval_WithADescriptionPerOperation()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 2, "extra hot");
        staging.Stage(Salad, 1, null);
        staging.SetMarkedForRemoval(LineOne, "1 × Bread", marked: true);

        StagedBatch batch = staging.Build(new CountingIdentifierFactory());

        Assert.Equal(3, batch.Operations.Count);
        Assert.Equal(3, batch.Descriptions.Count);

        LineAddedOperation first = Assert.IsType<LineAddedOperation>(batch.Operations[0]);
        Assert.Equal(SoupIdentifier, first.MenuItemIdentifier);
        Assert.Equal(2, first.Quantity);
        Assert.Equal("extra hot", first.CustomizationNote);

        LineAddedOperation second = Assert.IsType<LineAddedOperation>(batch.Operations[1]);
        Assert.Equal(SaladIdentifier, second.MenuItemIdentifier);

        LineRemovedOperation third = Assert.IsType<LineRemovedOperation>(batch.Operations[2]);
        Assert.Equal(LineOne, third.OrderLineIdentifier);

        Assert.Equal("2 × Soup", batch.Descriptions[0]);
        Assert.Equal("1 × Salad", batch.Descriptions[1]);
        Assert.Equal("Remove 1 × Bread", batch.Descriptions[2]);
    }

    [Fact]
    public void Build_MintsAFreshLineIdentifierForEachAddedLine()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 1, null);
        staging.Stage(Soup, 1, null);

        StagedBatch batch = staging.Build(new CountingIdentifierFactory());

        Guid[] identifiers = batch.Operations
            .OfType<LineAddedOperation>()
            .Select(operation => operation.OrderLineIdentifier)
            .ToArray();

        Assert.Equal(2, identifiers.Length);
        Assert.Equal(2, identifiers.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, identifiers);
    }

    [Fact]
    public void Build_ProposesEveryAddedLineAtZero_BecauseTheTransactionIsThePricingAuthority()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 3, null);

        StagedBatch batch = staging.Build(new CountingIdentifierFactory());

        Assert.Equal(0m, Assert.IsType<LineAddedOperation>(batch.Operations[0]).UnitPriceAmount);
    }

    [Fact]
    public void Build_SendsNoReasonWithAGuestRemoval()
    {
        OrderStaging staging = new();
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);

        StagedBatch batch = staging.Build(new CountingIdentifierFactory());

        Assert.Null(Assert.IsType<LineRemovedOperation>(batch.Operations[0]).Reason);
    }

    [Fact]
    public void Build_OnAnEmptyBasket_YieldsNoOperations()
    {
        StagedBatch batch = new OrderStaging().Build(new CountingIdentifierFactory());

        Assert.Empty(batch.Operations);
        Assert.Empty(batch.Descriptions);
    }

    [Fact]
    public void Clear_EmptiesBothHalves()
    {
        OrderStaging staging = new();
        staging.Stage(Soup, 1, null);
        staging.SetMarkedForRemoval(LineOne, "1 × Soup", marked: true);

        staging.Clear();

        Assert.True(staging.IsEmpty);
        Assert.Empty(staging.Lines);
        Assert.Empty(staging.Removals);
    }

    private static readonly Guid SectionIdentifier = Guid.Parse("0192f200-0000-7000-8000-0000000000c1");

    private static MenuItemSummary Item(Guid identifier, string name, decimal price, bool isActive)
        => new(
            identifier,
            SectionIdentifier,
            "Menu",
            string.Empty,
            true,
            name,
            string.Empty,
            price,
            0,
            isActive,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class CountingIdentifierFactory : IIdentifierFactory
    {
        private int _next;

        public Guid Create()
        {
            _next++;
            return Guid.Parse($"0192f200-0000-7000-8000-{_next:D12}");
        }
    }
}
