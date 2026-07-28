using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// §6.8's two visibility writes and the one thing that has to happen after each of them
/// (TECHNICAL_SPECIFICATION §6.8, §9, §11.1, §11.4). <see cref="OrdersWiringTests"/> covers the
/// registrations; this covers the behaviour.
///
/// <para>Every fact here is about something that fails <em>quietly</em>. A hide that committed without
/// publishing <see cref="VisibilityChanged"/> leaves the row on every other phone the guest has their
/// history open on — the exact moment they are watching for it to go. A refusal that published anyway
/// would make every subscriber re-query for a change that did not happen, and an <c>AlreadyHidden</c> that
/// published would re-announce somebody else's write. None of those raises an error anywhere.</para>
///
/// <para>No database and no container: <see cref="IOrderVisibility"/> is a hand-written fake (§16.1 —
/// hand-written fakes, no Moq) that answers with whatever outcome the fact needs. Arranging a genuine
/// already-hidden race against a real PostgreSQL would test the lock, and
/// <c>OrderVisibilityTests</c> already does that.</para>
/// </summary>
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

        // The arguments reached the service unchanged, and exactly once.
        Assert.Equal(OrderIdentifier, Assert.Single(visibility.HideCalls).Order);
        Assert.Equal(OwnerIdentifier, visibility.HideCalls[0].Actor);

        VisibilityChanged announced = Assert.IsType<VisibilityChanged>(
            Assert.Single(broadcaster.Published));

        Assert.Equal(OrderIdentifier, announced.GuestOrderIdentifier);
    }

    /// <summary>
    /// §9 keys <c>VisibilityChanged</c> on the order the service reports, not on the identifier the caller
    /// passed. They are the same today; asserting the former is what keeps them the same if the service
    /// ever resolves an order some other way.
    /// </summary>
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

    /// <summary>
    /// The four refusals, each publishing nothing. <c>AlreadyHidden</c> is in the list on purpose: it is
    /// not a failure — the order is in the state the person asked for — but nothing was written, so there
    /// is nothing to announce and whoever did write it announced it already.
    /// </summary>
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

        // The administrator is the actor, and it is their identifier the service is handed — not the
        // owner's, whom the result reports separately.
        Assert.Equal(OrderIdentifier, Assert.Single(visibility.UnhideCalls).Order);
        Assert.Equal(AdministratorIdentifier, visibility.UnhideCalls[0].Actor);
        Assert.Equal(OwnerIdentifier, result.OwnerPersonIdentifier);

        Assert.Equal(
            OrderIdentifier,
            Assert.IsType<VisibilityChanged>(Assert.Single(broadcaster.Published)).GuestOrderIdentifier);
    }

    /// <summary>
    /// <c>NotHidden</c> is the losing side of two administrators pressing Unhide at once. Nothing was
    /// written, so nothing is announced — the winner's broadcast already went out.
    /// </summary>
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

    /// <summary>
    /// The two operations are independent: hiding then unhiding the same order announces twice, once each.
    /// A shell that deduplicated would leave the second change unheard.
    /// </summary>
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

    /// <summary>
    /// Answers with whatever outcome the fact under test needs, and records what it was asked. Hand-written
    /// rather than mocked (§16.1, F-20).
    /// </summary>
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
                // Nothing subscribes in these tests; the token exists only to satisfy the contract.
            }
        }
    }
}
