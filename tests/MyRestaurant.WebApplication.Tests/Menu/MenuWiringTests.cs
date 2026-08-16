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
    private static readonly Guid MenuSectionIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000d003");

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
    ///
    /// <para><b>One of them now has a surface and the other four verbs still do not.</b>
    /// <c>CreateMenuSection.razor</c> takes <see cref="IMenuWorkflow"/> and reaches
    /// <see cref="IMenuSectionAdministration.CreateMenuSectionAsync"/> through it; rename, describe,
    /// reorder and set-active are still called by nothing at all, which is why the raw interface is still
    /// asserted here rather than left to the first surface that needs it.</para>
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
    /// One workflow over all three write services. §9 does not distinguish which verb changed the menu,
    /// and every subscriber responds to <see cref="MenuChanged"/> the same way, so a second workflow would
    /// only make it possible to wire an application that announces 86s and not repricings.
    /// </summary>
    [Fact]
    public void MenuWorkflow_IsResolvableInAScope_AndCoversEveryWriteService()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<MenuWorkflow>(scope.ServiceProvider.GetRequiredService<IMenuWorkflow>());
        Assert.IsType<DapperMenuAvailability>(scope.ServiceProvider.GetRequiredService<IMenuAvailability>());
        Assert.IsType<DapperMenuAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuAdministration>());
        Assert.IsType<DapperMenuSectionAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuSectionAdministration>());
    }

    [Fact]
    public async Task ACreatedItem_IsAnnounced_AndItsArgumentsArePassedThrough()
    {
        FakeMenuAdministration administration = new();
        RecordingBroadcaster broadcaster = new();

        CreateMenuItemResult result = await WorkflowOver(administration, broadcaster).CreateMenuItemAsync(
            MenuItemIdentifier,
            MenuSectionIdentifier,
            "Soup",
            "Lentil, vegan",
            4.50m,
            ActorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(MenuItemIdentifier, administration.LastMenuItemIdentifier);
        Assert.Equal(MenuSectionIdentifier, administration.LastMenuSectionIdentifier);
        Assert.Equal("Soup", administration.LastName);
        Assert.Equal("Lentil, vegan", administration.LastDescription);
        Assert.Equal(4.50m, administration.LastPriceAmount);
        Assert.Equal(ActorIdentifier, administration.LastActor);

        // The result is passed through untouched — the surface echoes the stored name, description and
        // price back. The description and the section matter here rather than being decoration: this
        // workflow is the one place that could silently drop an argument between a form and a
        // transaction, and §7 makes the section the one argument a create cannot do without.
        Assert.Equal("Soup", result.Name);
        Assert.Equal("Lentil, vegan", result.Description);
        Assert.Equal(4.50m, result.PriceAmount);
        Assert.Equal(MenuSectionIdentifier, result.MenuSectionIdentifier);

        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

    /// <summary>
    /// The publish that stopped being unconditional (<c>0005</c>). A create used to commit or throw, so
    /// this file's rule — announce only what committed — had nothing to catch here. §7's mandatory
    /// heading makes "that section does not exist" an ordinary reported outcome, and announcing it would
    /// send every phone, kitchen board and display in the building back to the database for an item that
    /// was never written.
    /// </summary>
    [Fact]
    public async Task AnItemUnderAMissingSection_AnnouncesNothing()
    {
        FakeMenuAdministration administration = new()
        {
            CreateOutcome = CreateMenuItemOutcome.MenuSectionNotFound,
        };
        RecordingBroadcaster broadcaster = new();

        CreateMenuItemResult result = await WorkflowOver(administration, broadcaster).CreateMenuItemAsync(
            MenuItemIdentifier,
            MenuSectionIdentifier,
            "Soup",
            description: null,
            4.50m,
            ActorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Empty(broadcaster.Published);
    }

    /// <summary>
    /// The first of <see cref="IMenuSectionAdministration"/>'s five verbs to come behind the workflow,
    /// and the one that has a surface: <c>CreateMenuSection.razor</c>. The obligation to bring the other
    /// four in is narrowed rather than closed, and it is a real one as of this slice — §11.1's guest menu
    /// groups by heading now, so a rename that announced nothing would leave a stale heading in every
    /// open picker.
    ///
    /// <para>Announced conditionally, because a section create can fail on the <c>citext</c> UNIQUE. A
    /// second "Drinks" spelled any way at all commits nothing.</para>
    /// </summary>
    [Fact]
    public async Task ACreatedSection_IsAnnouncedOnlyWhenARowWasWritten()
    {
        FakeMenuSectionAdministration created = new();
        RecordingBroadcaster createdBroadcaster = new();

        CreateMenuSectionResult result = await WorkflowOver(created, createdBroadcaster)
            .CreateMenuSectionAsync(
                MenuSectionIdentifier,
                "Drinks",
                "Cold things",
                ActorIdentifier,
                TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Equal(MenuSectionIdentifier, created.LastMenuSectionIdentifier);
        Assert.Equal("Drinks", created.LastName);
        Assert.Equal("Cold things", created.LastDescription);
        Assert.Equal(ActorIdentifier, created.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(createdBroadcaster.Published));

        FakeMenuSectionAdministration taken = new()
        {
            CreateResult = new CreateMenuSectionResult(
                CreateMenuSectionOutcome.NameTaken, MenuSectionIdentifier, null, null, null),
        };
        RecordingBroadcaster takenBroadcaster = new();

        CreateMenuSectionResult refused = await WorkflowOver(taken, takenBroadcaster)
            .CreateMenuSectionAsync(
                MenuSectionIdentifier,
                "drinks",
                description: null,
                ActorIdentifier,
                TestContext.Current.CancellationToken);

        Assert.False(refused.Created);
        Assert.Empty(takenBroadcaster.Published);
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

    /// <summary>
    /// A description that moved is announced; one that did not is not. Same rule as rename and reprice,
    /// and it is asserted for the same reason: <c>MenuChanged</c> tells every open surface in the building
    /// to re-query, and doing that for a write that committed nothing is the failure this file exists to
    /// prevent.
    /// </summary>
    [Fact]
    public async Task ADescription_IsAnnouncedOnlyWhenItActuallyMoved()
    {
        FakeMenuAdministration described = new() { DescribeOutcome = DescribeMenuItemOutcome.Described };
        RecordingBroadcaster describedBroadcaster = new();

        Assert.Equal(
            DescribeMenuItemOutcome.Described,
            await WorkflowOver(described, describedBroadcaster).DescribeMenuItemAsync(
                MenuItemIdentifier, "Lentil", ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal("Lentil", described.LastDescription);
        Assert.IsType<MenuChanged>(Assert.Single(describedBroadcaster.Published));

        FakeMenuAdministration unchanged = new() { DescribeOutcome = DescribeMenuItemOutcome.NoChange };
        RecordingBroadcaster unchangedBroadcaster = new();

        Assert.Equal(
            DescribeMenuItemOutcome.NoChange,
            await WorkflowOver(unchanged, unchangedBroadcaster).DescribeMenuItemAsync(
                MenuItemIdentifier, "Lentil", ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Empty(unchangedBroadcaster.Published);
    }

    /// <summary>
    /// A move that committed is announced, because §11.1 and §11.2 both render the menu in display order —
    /// so the pickers show something different even though no item's name, price or availability moved.
    /// </summary>
    [Fact]
    public async Task AMove_IsAnnouncedOnlyWhenThePositionActuallyMoved()
    {
        FakeMenuAdministration moved = new() { ReorderOutcome = ReorderMenuItemOutcome.Reordered };
        RecordingBroadcaster movedBroadcaster = new();

        Assert.Equal(
            ReorderMenuItemOutcome.Reordered,
            await WorkflowOver(moved, movedBroadcaster).ReorderMenuItemAsync(
                MenuItemIdentifier, 3, ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal(3, moved.LastDisplayOrder);
        Assert.IsType<MenuChanged>(Assert.Single(movedBroadcaster.Published));

        FakeMenuAdministration unchanged = new() { ReorderOutcome = ReorderMenuItemOutcome.NoChange };
        RecordingBroadcaster unchangedBroadcaster = new();

        Assert.Equal(
            ReorderMenuItemOutcome.NoChange,
            await WorkflowOver(unchanged, unchangedBroadcaster).ReorderMenuItemAsync(
                MenuItemIdentifier, 3, ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Empty(unchangedBroadcaster.Published);
    }

    // Three overloads, distinguished by their first parameter: whichever write service the test is about
    // is the one it passes, and the others are default fakes nothing under test ever calls.
    private static MenuWorkflow WorkflowOver(
        IMenuAdministration administration,
        IDomainEventBroadcaster broadcaster)
        => new(new FakeMenuAvailability(), administration, new FakeMenuSectionAdministration(), broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuAvailability availability,
        IDomainEventBroadcaster broadcaster)
        => new(availability, new FakeMenuAdministration(), new FakeMenuSectionAdministration(), broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuSectionAdministration sections,
        IDomainEventBroadcaster broadcaster)
        => new(new FakeMenuAvailability(), new FakeMenuAdministration(), sections, broadcaster);

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

        public string? LastDescription { get; private set; }

        public Guid? LastMenuSectionIdentifier { get; private set; }

        public decimal? LastPriceAmount { get; private set; }

        public int? LastDisplayOrder { get; private set; }

        public Guid? LastActor { get; private set; }

        public RenameMenuItemResult RenameResult { get; init; } = new(
            RenameMenuItemOutcome.Renamed, MenuItemIdentifier, "Broth", "Soup");

        public RepriceMenuItemResult RepriceResult { get; init; } = new(
            RepriceMenuItemOutcome.Repriced, MenuItemIdentifier, "Soup", 5.00m, 4.50m);

        public DescribeMenuItemOutcome DescribeOutcome { get; init; } = DescribeMenuItemOutcome.Described;

        public ReorderMenuItemOutcome ReorderOutcome { get; init; } = ReorderMenuItemOutcome.Reordered;

        public CreateMenuItemOutcome CreateOutcome { get; init; } = CreateMenuItemOutcome.Created;

        public Task<CreateMenuItemResult> CreateMenuItemAsync(
            Guid menuItemIdentifier,
            Guid menuSectionIdentifier,
            string name,
            string? description,
            decimal priceAmount,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastName = name;
            LastDescription = description;
            LastPriceAmount = priceAmount;
            LastActor = actorPersonIdentifier;

            // The real service normalizes null and blank to "". This fake deliberately does not: what is
            // under test here is whether the workflow hands the argument on unchanged, and a fake that
            // trimmed would hide the one failure this file can see.
            return Task.FromResult(CreateOutcome is CreateMenuItemOutcome.Created
                ? new CreateMenuItemResult(
                    CreateMenuItemOutcome.Created,
                    menuItemIdentifier,
                    menuSectionIdentifier,
                    "Mains",
                    name,
                    description ?? string.Empty,
                    priceAmount,
                    DisplayOrder: 0)
                : new CreateMenuItemResult(
                    CreateOutcome,
                    menuItemIdentifier,
                    menuSectionIdentifier,
                    MenuSectionName: null,
                    Name: null,
                    Description: null,
                    PriceAmount: null,
                    DisplayOrder: null));
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

        public Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
            Guid menuItemIdentifier,
            string? description,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastDescription = description;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(DescribeOutcome);
        }

        public Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
            Guid menuItemIdentifier,
            int displayOrder,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastDisplayOrder = displayOrder;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(ReorderOutcome);
        }
    }

    /// <summary>
    /// The section write service, of which the workflow uses exactly one verb. The other four throw
    /// rather than return a plausible value: this fake is reachable from every test in this file through
    /// the default overloads, and a verb that quietly answered would let a workflow start calling it
    /// without anything here noticing.
    /// </summary>
    private sealed class FakeMenuSectionAdministration : IMenuSectionAdministration
    {
        public Guid? LastMenuSectionIdentifier { get; private set; }

        public string? LastName { get; private set; }

        public string? LastDescription { get; private set; }

        public Guid? LastActor { get; private set; }

        public CreateMenuSectionResult CreateResult { get; init; } = new(
            CreateMenuSectionOutcome.Created, MenuSectionIdentifier, "Drinks", "Cold things", 0);

        public Task<CreateMenuSectionResult> CreateMenuSectionAsync(
            Guid menuSectionIdentifier,
            string name,
            string? description,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastName = name;
            LastDescription = description;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(CreateResult);
        }

        public Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
            Guid menuSectionIdentifier,
            string name,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Unreachable(nameof(RenameMenuSectionAsync)));

        public Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
            Guid menuSectionIdentifier,
            string? description,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Unreachable(nameof(DescribeMenuSectionAsync)));

        public Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
            Guid menuSectionIdentifier,
            int displayOrder,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Unreachable(nameof(ReorderMenuSectionAsync)));

        public Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
            Guid menuSectionIdentifier,
            bool isActive,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Unreachable(nameof(SetMenuSectionActiveAsync)));

        private static string Unreachable(string verb)
            => $"MenuWorkflow reached IMenuSectionAdministration.{verb}, which no surface calls yet."
                + " If a surface now does, that verb belongs behind IMenuWorkflow with a §9 broadcast"
                + " and a fact in this file — see docs/MENU_AND_HANDHELD_PLAN.md Stage 3.";
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
