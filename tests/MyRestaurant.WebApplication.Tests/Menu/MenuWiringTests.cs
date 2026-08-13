using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Menu;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// The menu half of the wiring composed by
/// <see cref="OrdersServiceCollectionExtensions.AddRestaurantOrders"/>, plus the behaviour of
/// <see cref="MenuWorkflow"/> itself (TECHNICAL_SPECIFICATION §7, §9, §11.4).
/// <see cref="KitchenWiringTests"/> covers the 86 toggle's registration from the kitchen's side; this
/// covers the administration writes and, more importantly, <em>which</em> calls announce themselves.
///
/// <para>The behavioural facts here are the ones that fail <em>quietly</em>. A reprice that committed but
/// published nothing leaves every open guest picker quoting yesterday's price until that page happens to
/// reload, and the guest is then surprised at the till by a number nobody showed them — §9's
/// <see cref="MenuChanged"/> is the only signal that reaches those pages. The mirror-image bug is just as
/// silent: announcing a rename that changed nothing tells every phone, every kitchen board, and every
/// display in the building to re-query the menu because somebody pressed a button and nothing
/// happened.</para>
///
/// <para>No database and no container: the two write services are hand-written fakes (§16.1 — hand-written
/// fakes, no Moq) returning whatever outcome the test wants to react to. Arranging a genuine no-op against
/// a real PostgreSQL would test the lock and the comparison, which
/// <c>MenuAdministrationTests</c> and <c>MenuAvailabilityTests</c> already do.</para>
/// </summary>
public sealed class MenuWiringTests
{
    private static readonly Guid MenuItemIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000d001");
    private static readonly Guid ActorIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000d002");

    [Fact]
    public void MenuAdministration_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuAdministration>());
    }

    [Fact]
    public void MenuEventLog_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuEventLog>(scope.ServiceProvider.GetRequiredService<IMenuEventLog>());
    }

    /// <summary>
    /// The two section services resolve from the same registration call as the item services (§7, §11.4).
    /// They are asserted here rather than left to the first surface that needs them, because they are the
    /// one pair in this group that <em>no</em> surface takes yet: Stage 2 landed the schema and the data
    /// access, and Stage 3 writes the pages. An unwired service with no caller fails at the moment
    /// somebody writes the caller, which is the worst time to find out.
    /// </summary>
    [Fact]
    public void MenuSectionServices_AreResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuSectionDirectory>(
            scope.ServiceProvider.GetRequiredService<IMenuSectionDirectory>());
        Assert.IsType<DapperMenuSectionAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuSectionAdministration>());
    }

    /// <summary>
    /// One workflow over both write services. §9 does not distinguish which verb changed the menu, and
    /// every subscriber responds to <see cref="MenuChanged"/> the same way, so a second workflow would only
    /// make it possible to wire an application that announces 86s and not repricings.
    /// </summary>
    [Fact]
    public void MenuWorkflow_IsResolvableInAScope_AndCoversBothWriteServices()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<MenuWorkflow>(scope.ServiceProvider.GetRequiredService<IMenuWorkflow>());
        Assert.IsType<DapperMenuAvailability>(scope.ServiceProvider.GetRequiredService<IMenuAvailability>());
        Assert.IsType<DapperMenuAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuAdministration>());
    }

    [Fact]
    public async Task ACreatedItem_IsAlwaysAnnounced_AndItsArgumentsArePassedThrough()
    {
        FakeMenuAdministration administration = new();
        RecordingBroadcaster broadcaster = new();

        CreateMenuItemResult result = await WorkflowOver(administration, broadcaster).CreateMenuItemAsync(
            MenuItemIdentifier,
            "Soup",
            4.50m,
            ActorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(MenuItemIdentifier, administration.LastMenuItemIdentifier);
        Assert.Equal("Soup", administration.LastName);
        Assert.Equal(4.50m, administration.LastPriceAmount);
        Assert.Equal(ActorIdentifier, administration.LastActor);

        // The result is passed through untouched — the surface echoes the stored name and price back.
        Assert.Equal("Soup", result.Name);
        Assert.Equal(4.50m, result.PriceAmount);

        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

    [Fact]
    public async Task ARename_IsAnnouncedOnlyWhenTheNameActuallyMoved()
    {
        FakeMenuAdministration changed = new()
        {
            RenameResult = new RenameMenuItemResult(
                RenameMenuItemOutcome.Renamed, MenuItemIdentifier, "Broth", "Soup"),
        };
        RecordingBroadcaster changedBroadcaster = new();

        RenameMenuItemResult renamed = await WorkflowOver(changed, changedBroadcaster).RenameMenuItemAsync(
            MenuItemIdentifier, "Broth", ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.True(renamed.Changed);
        Assert.Equal("Soup", renamed.PreviousName);
        Assert.IsType<MenuChanged>(Assert.Single(changedBroadcaster.Published));

        FakeMenuAdministration unchanged = new()
        {
            RenameResult = new RenameMenuItemResult(
                RenameMenuItemOutcome.NoChange, MenuItemIdentifier, "Soup", "Soup"),
        };
        RecordingBroadcaster unchangedBroadcaster = new();

        RenameMenuItemResult noChange = await WorkflowOver(unchanged, unchangedBroadcaster).RenameMenuItemAsync(
            MenuItemIdentifier, "Soup", ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.False(noChange.Changed);
        Assert.True(noChange.ItemExists);
        Assert.Empty(unchangedBroadcaster.Published);
    }

    [Fact]
    public async Task AReprice_IsAnnouncedOnlyWhenThePriceActuallyMoved()
    {
        FakeMenuAdministration changed = new()
        {
            RepriceResult = new RepriceMenuItemResult(
                RepriceMenuItemOutcome.Repriced, MenuItemIdentifier, "Soup", 5.00m, 4.50m),
        };
        RecordingBroadcaster changedBroadcaster = new();

        RepriceMenuItemResult repriced = await WorkflowOver(changed, changedBroadcaster).RepriceMenuItemAsync(
            MenuItemIdentifier, 5.00m, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.True(repriced.Changed);
        Assert.Equal(4.50m, repriced.PreviousPriceAmount);
        Assert.IsType<MenuChanged>(Assert.Single(changedBroadcaster.Published));

        FakeMenuAdministration unchanged = new()
        {
            RepriceResult = new RepriceMenuItemResult(
                RepriceMenuItemOutcome.NoChange, MenuItemIdentifier, "Soup", 4.50m, 4.50m),
        };
        RecordingBroadcaster unchangedBroadcaster = new();

        await WorkflowOver(unchanged, unchangedBroadcaster).RepriceMenuItemAsync(
            MenuItemIdentifier, 4.50m, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(unchangedBroadcaster.Published);
    }

    /// <summary>
    /// A stale management page, or a link somebody kept. Nothing was written, so nothing may be announced —
    /// and the surface above turns this into "that item no longer exists" rather than a silent success.
    /// </summary>
    [Fact]
    public async Task AnUnknownItem_AnnouncesNothing()
    {
        FakeMenuAdministration administration = new()
        {
            RenameResult = new RenameMenuItemResult(
                RenameMenuItemOutcome.MenuItemNotFound, MenuItemIdentifier, null, null),
            RepriceResult = new RepriceMenuItemResult(
                RepriceMenuItemOutcome.MenuItemNotFound, MenuItemIdentifier, null, null, null),
        };
        RecordingBroadcaster broadcaster = new();
        IMenuWorkflow workflow = WorkflowOver(administration, broadcaster);

        await workflow.RenameMenuItemAsync(
            MenuItemIdentifier, "Broth", ActorIdentifier, TestContext.Current.CancellationToken);
        await workflow.RepriceMenuItemAsync(
            MenuItemIdentifier, 5.00m, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(broadcaster.Published);
    }

    /// <summary>
    /// The 86 half, asserted here rather than only in <see cref="KitchenWiringTests"/> because it is the
    /// same rule and it now shares a class with three other verbs: a toggle to the state the item is
    /// already in — two cooks pressing 86 seconds apart — committed nothing and must announce nothing.
    /// </summary>
    [Fact]
    public async Task An86_IsAnnouncedOnlyWhenTheFlagActuallyMoved()
    {
        FakeMenuAvailability changed = new(new SetMenuItemAvailabilityResult(
            SetMenuItemAvailabilityOutcome.Changed, MenuItemIdentifier, "Soup", IsActive: false));
        RecordingBroadcaster changedBroadcaster = new();

        await WorkflowOver(changed, changedBroadcaster).SetMenuItemActiveAsync(
            MenuItemIdentifier, isActive: false, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.IsType<MenuChanged>(Assert.Single(changedBroadcaster.Published));

        FakeMenuAvailability unchanged = new(new SetMenuItemAvailabilityResult(
            SetMenuItemAvailabilityOutcome.AlreadyInThatState, MenuItemIdentifier, "Soup", IsActive: false));
        RecordingBroadcaster unchangedBroadcaster = new();

        await WorkflowOver(unchanged, unchangedBroadcaster).SetMenuItemActiveAsync(
            MenuItemIdentifier, isActive: false, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(unchangedBroadcaster.Published);
    }

    // Two overloads, distinguished by their first parameter: whichever write service the test is about
    // is the one it passes, and the other is a default fake nothing under test ever calls.
    private static MenuWorkflow WorkflowOver(
        IMenuAdministration administration,
        IDomainEventBroadcaster broadcaster)
        => new(new FakeMenuAvailability(), administration, broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuAvailability availability,
        IDomainEventBroadcaster broadcaster)
        => new(availability, new FakeMenuAdministration(), broadcaster);

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantOrders. The connection factory is
        // never used — resolution constructs, it does not connect — and the hosted service registered by
        // AddRestaurantOrders needs logging and the bound options, neither of which starts here.
        services.AddLogging();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton<IDomainEventBroadcaster, RecordingBroadcaster>();
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();

        services.AddRestaurantOrders();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }

    private sealed class FakeMenuAdministration : IMenuAdministration
    {
        public Guid? LastMenuItemIdentifier { get; private set; }

        public string? LastName { get; private set; }

        public decimal? LastPriceAmount { get; private set; }

        public Guid? LastActor { get; private set; }

        public RenameMenuItemResult RenameResult { get; init; } = new(
            RenameMenuItemOutcome.Renamed, MenuItemIdentifier, "Broth", "Soup");

        public RepriceMenuItemResult RepriceResult { get; init; } = new(
            RepriceMenuItemOutcome.Repriced, MenuItemIdentifier, "Soup", 5.00m, 4.50m);

        public Task<CreateMenuItemResult> CreateMenuItemAsync(
            Guid menuItemIdentifier,
            string name,
            decimal priceAmount,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastName = name;
            LastPriceAmount = priceAmount;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(new CreateMenuItemResult(menuItemIdentifier, name, priceAmount));
        }

        public Task<RenameMenuItemResult> RenameMenuItemAsync(
            Guid menuItemIdentifier,
            string name,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastName = name;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(RenameResult);
        }

        public Task<RepriceMenuItemResult> RepriceMenuItemAsync(
            Guid menuItemIdentifier,
            decimal priceAmount,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastPriceAmount = priceAmount;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(RepriceResult);
        }
    }

    private sealed class FakeMenuAvailability : IMenuAvailability
    {
        private readonly SetMenuItemAvailabilityResult _result;

        public FakeMenuAvailability()
            : this(new SetMenuItemAvailabilityResult(
                SetMenuItemAvailabilityOutcome.Changed, MenuItemIdentifier, "Soup", IsActive: false))
        {
        }

        public FakeMenuAvailability(SetMenuItemAvailabilityResult result) => _result = result;

        public Task<SetMenuItemAvailabilityResult> SetActiveAsync(
            Guid menuItemIdentifier,
            bool isActive,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
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
