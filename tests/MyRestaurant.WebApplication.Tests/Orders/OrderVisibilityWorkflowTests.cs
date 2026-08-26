using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class OrderVisibilityWorkflowTests
{
    private static readonly Guid OrderIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000f0001");
    private static readonly Guid OwnerIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000f00a1");
    private static readonly Guid AdministratorIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000f00a2");
    private static readonly Guid SittingIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000f00b1");
    private static readonly DateTimeOffset OccurredAt = new(2026, 6, 11, 21, 40, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hide_WhenTheRowWasAppended_AnnouncesTheChangeOnce()
    {
        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.HideWill = new HideOrderResult(
            HideOrderOutcome.Hidden, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt);

        HideOrderResult result = await workflow.HideAsync(
            OrderIdentifier, OwnerIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(HideOrderOutcome.Hidden, result.Outcome);

        Assert.Equal(OrderIdentifier, Assert.Single(visibility.HideCalls).Order);
        Assert.Equal(OwnerIdentifier, visibility.HideCalls[0].Actor);

        VisibilityChanged announced = Assert.IsType<VisibilityChanged>(
            Assert.Single(broadcaster.Published));

        Assert.Equal(OrderIdentifier, announced.GuestOrderIdentifier);
    }

    [Fact]
    public async Task Hide_AnnouncesTheOrderTheServiceReported()
    {
        Guid resolved = Guid.Parse("0192f000-0000-7000-8000-0000000f0002");

        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.HideWill = new HideOrderResult(
            HideOrderOutcome.Hidden, resolved, SittingIdentifier, OwnerIdentifier, OccurredAt);

        await workflow.HideAsync(OrderIdentifier, OwnerIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(
            resolved,
            Assert.IsType<VisibilityChanged>(Assert.Single(broadcaster.Published)).GuestOrderIdentifier);
    }

    [Theory]
    [InlineData(HideOrderOutcome.AlreadyHidden)]
    [InlineData(HideOrderOutcome.NotTheOwner)]
    [InlineData(HideOrderOutcome.SittingStillOpen)]
    [InlineData(HideOrderOutcome.OrderNotFound)]
    public async Task Hide_WhenNothingWasWritten_AnnouncesNothing(HideOrderOutcome outcome)
    {
        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.HideWill = new HideOrderResult(
            outcome, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt: null);

        HideOrderResult result = await workflow.HideAsync(
            OrderIdentifier, OwnerIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.IsHidden);
        Assert.Empty(broadcaster.Published);
    }

    [Fact]
    public async Task Unhide_WhenTheRowWasAppended_AnnouncesTheChangeOnce()
    {
        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.UnhideWill = new UnhideOrderResult(
            UnhideOrderOutcome.Unhidden, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt);

        UnhideOrderResult result = await workflow.UnhideAsync(
            OrderIdentifier, AdministratorIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(UnhideOrderOutcome.Unhidden, result.Outcome);

        Assert.Equal(OrderIdentifier, Assert.Single(visibility.UnhideCalls).Order);
        Assert.Equal(AdministratorIdentifier, visibility.UnhideCalls[0].Actor);
        Assert.Equal(OwnerIdentifier, result.OwnerPersonIdentifier);

        Assert.Equal(
            OrderIdentifier,
            Assert.IsType<VisibilityChanged>(Assert.Single(broadcaster.Published)).GuestOrderIdentifier);
    }

    [Theory]
    [InlineData(UnhideOrderOutcome.NotHidden)]
    [InlineData(UnhideOrderOutcome.OrderNotFound)]
    public async Task Unhide_WhenNothingWasWritten_AnnouncesNothing(UnhideOrderOutcome outcome)
    {
        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.UnhideWill = new UnhideOrderResult(
            outcome, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt: null);

        UnhideOrderResult result = await workflow.UnhideAsync(
            OrderIdentifier, AdministratorIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.IsUnhidden);
        Assert.Empty(broadcaster.Published);
    }

    [Fact]
    public async Task HideThenUnhide_AnnouncesTwice()
    {
        FakeOrderVisibility visibility = new();
        RecordingBroadcaster broadcaster = new();
        OrderVisibilityWorkflow workflow = new(visibility, broadcaster);

        visibility.HideWill = new HideOrderResult(
            HideOrderOutcome.Hidden, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt);
        visibility.UnhideWill = new UnhideOrderResult(
            UnhideOrderOutcome.Unhidden, OrderIdentifier, SittingIdentifier, OwnerIdentifier, OccurredAt);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await workflow.HideAsync(OrderIdentifier, OwnerIdentifier, cancellationToken);
        await workflow.UnhideAsync(OrderIdentifier, AdministratorIdentifier, cancellationToken);

        Assert.Equal(2, broadcaster.Published.Count);
        Assert.All(broadcaster.Published, notification => Assert.IsType<VisibilityChanged>(notification));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        RecordingBroadcaster broadcaster = new();
        FakeOrderVisibility visibility = new();

        Assert.Throws<ArgumentNullException>(() => new OrderVisibilityWorkflow(null!, broadcaster));
        Assert.Throws<ArgumentNullException>(() => new OrderVisibilityWorkflow(visibility, null!));
    }

    private sealed class FakeOrderVisibility : IOrderVisibility
    {
        public HideOrderResult? HideWill { get; set; }

        public UnhideOrderResult? UnhideWill { get; set; }

        public List<(Guid Order, Guid Actor)> HideCalls { get; } = [];

        public List<(Guid Order, Guid Actor)> UnhideCalls { get; } = [];

        public Task<HideOrderResult> HideAsync(
            Guid guestOrderIdentifier,
            Guid ownerPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            HideCalls.Add((guestOrderIdentifier, ownerPersonIdentifier));

            return Task.FromResult(
                HideWill ?? throw new InvalidOperationException("Arrange a hide outcome first."));
        }

        public Task<UnhideOrderResult> UnhideAsync(
            Guid guestOrderIdentifier,
            Guid administratorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            UnhideCalls.Add((guestOrderIdentifier, administratorPersonIdentifier));

            return Task.FromResult(
                UnhideWill ?? throw new InvalidOperationException("Arrange an unhide outcome first."));
        }
    }

    private sealed class RecordingBroadcaster : IDomainEventBroadcaster
    {
        public List<DomainNotification> Published { get; } = [];

        public void Publish(DomainNotification notification) => Published.Add(notification);

        public IDisposable Subscribe(Action<DomainNotification> handler) => new NoSubscription();

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
