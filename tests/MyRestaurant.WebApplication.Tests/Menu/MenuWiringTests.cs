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
/// <para>No database and no container: the three write services are hand-written fakes (§16.1 —
/// hand-written fakes, no Moq) returning whatever outcome the test wants to react to. Arranging a genuine
/// no-op against a real PostgreSQL would test the lock and the comparison, which
/// <c>MenuAdministrationTests</c>, <c>MenuAvailabilityTests</c> and
/// <c>MenuSectionAdministrationTests</c> already do.</para>
///
/// <para><b>Every one of <see cref="IMenuSectionAdministration"/>'s six verbs is asserted here</b>, and
/// four of them used to make <c>FakeMenuSectionAdministration</c> throw. That
/// throw was the guard on a stated obligation — a workflow verb with no caller is a code path no test can
/// reach through the interface meant to protect it — and it is gone because the obligation is discharged
/// rather than because it became inconvenient. The two that matter most are the ones §11.1 made expensive:
/// a heading's name is rendered above every card under it, and §7 removes an inactive heading from the
/// guest's menu <em>entirely</em>.</para>
///
/// <para><b>And with the refile there is no verb on <see cref="IMenuWorkflow"/> left without a surface.</b>
/// <c>MoveMenuItemToSectionAsync</c> was named as outstanding in three consecutive slices rather than
/// quietly omitted, which is the whole reason its arrival can be stated as a fact instead of noticed
/// later. Every method this file exercises is reachable from a form an administrator can open.</para>
///
/// <para><b><c>ResequenceMenuSectionsAsync</c> arrives the same way and is the sixth.</b> It was specified
/// in the plan and deferred by name for two slices — once for F-95, whose fix it depends on, and once for
/// the dump reduction — and it arrives with its caller: the Up and Down controls on the menu index. Its two
/// facts below are the pair every verb here gets, and the second is worth reading because
/// <c>MenuSectionSetChanged</c> is a <em>third</em> way to write nothing: not "the value did not move" but
/// "the list did not describe this menu", which is a stale page rather than a no-op and announces nothing
/// for the same reason.</para>
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
    /// The three section services resolve from the same registration call as the item services (§7,
    /// §11.4).
    ///
    /// <para><b>All five section verbs have a surface now, and the obligation carried since Slice 37 is
    /// closed.</b> <c>CreateMenuSection.razor</c> reached <c>CreateMenuSectionAsync</c> through
    /// <see cref="IMenuWorkflow"/> with <c>0005</c>; <c>ManageMenuSection.razor</c> brings rename,
    /// describe, reorder and set-active in together, because they are four forms on one page.
    /// <see cref="IMenuSectionAdministration"/> is still asserted here because
    /// <see cref="MenuWorkflow"/> takes it as a dependency — what changed is that no surface resolves it,
    /// exactly as none resolves <see cref="IMenuAdministration"/> or
    /// <see cref="IMenuAvailability"/>.</para>
    ///
    /// <para><see cref="IMenuSectionEventLog"/> is the read the editor could not have shipped without:
    /// §11.4 renders a heading's complete uncapped history on its own page, and until this slice nothing
    /// in the tree could read <c>menu_section_event</c> at all.</para>
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
        Assert.IsType<DapperMenuSectionEventLog>(
            scope.ServiceProvider.GetRequiredService<IMenuSectionEventLog>());
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

    /// <summary>
    /// A section rename that committed is announced; one that changed nothing, one refused for a name
    /// collision, and one against a section that is not there are not.
    ///
    /// <para><b>This publish matters more than an item rename's, and §11.1 is why.</b> The guest menu
    /// groups items under their headings, so the heading's name is rendered on every open picker in the
    /// building — and unlike an item's name it is rendered even when nothing under it changed. A rename
    /// that committed and announced nothing would leave the old word on every phone until that page
    /// happened to reload, which was a latent defect from Slice 40 until this slice gave the verb a
    /// surface.</para>
    ///
    /// <para>The <c>NameTaken</c> arm is asserted rather than folded into the no-op one because it is a
    /// different reason for the same silence: the column is <c>citext</c>, so a second "Drinks" spelled
    /// any way at all is refused by the database and the transaction rolls back. A workflow that keyed on
    /// "not NoChange" would announce it.</para>
    /// </summary>
    [Fact]
    public async Task ASectionRename_IsAnnouncedOnlyWhenTheNameActuallyMoved()
    {
        FakeMenuSectionAdministration renamed = new()
        {
            RenameOutcome = RenameMenuSectionOutcome.Renamed,
        };
        RecordingBroadcaster renamedBroadcaster = new();

        Assert.Equal(
            RenameMenuSectionOutcome.Renamed,
            await WorkflowOver(renamed, renamedBroadcaster).RenameMenuSectionAsync(
                MenuSectionIdentifier, "Puddings", ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal(MenuSectionIdentifier, renamed.LastMenuSectionIdentifier);
        Assert.Equal("Puddings", renamed.LastName);
        Assert.Equal(ActorIdentifier, renamed.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(renamedBroadcaster.Published));

        foreach (RenameMenuSectionOutcome silent in new[]
        {
            RenameMenuSectionOutcome.NoChange,
            RenameMenuSectionOutcome.NameTaken,
            RenameMenuSectionOutcome.MenuSectionNotFound,
        })
        {
            FakeMenuSectionAdministration sections = new() { RenameOutcome = silent };
            RecordingBroadcaster broadcaster = new();

            await WorkflowOver(sections, broadcaster).RenameMenuSectionAsync(
                MenuSectionIdentifier, "Puddings", ActorIdentifier, TestContext.Current.CancellationToken);

            Assert.Empty(broadcaster.Published);
        }
    }

    /// <summary>
    /// A section description that moved is announced; one that did not is not.
    ///
    /// <para><b>Today this publish reaches no guest surface, and it is still the right call</b> — the same
    /// argument the item description makes, and it was right then. §11.1 renders a heading's name and not
    /// its description, because the guest menu groups from <c>MenuItemSummary</c>, which carries the one
    /// and not the other. <c>MenuChanged</c> means "re-read the menu" and nothing else, and a workflow
    /// that decided which columns were worth announcing would be a workflow that has to be edited again
    /// the moment a surface starts reading one.</para>
    /// </summary>
    [Fact]
    public async Task ASectionDescription_IsAnnouncedOnlyWhenItActuallyMoved()
    {
        FakeMenuSectionAdministration described = new()
        {
            DescribeOutcome = DescribeMenuSectionOutcome.Described,
        };
        RecordingBroadcaster describedBroadcaster = new();

        Assert.Equal(
            DescribeMenuSectionOutcome.Described,
            await WorkflowOver(described, describedBroadcaster).DescribeMenuSectionAsync(
                MenuSectionIdentifier,
                "Served until 11am",
                ActorIdentifier,
                TestContext.Current.CancellationToken));

        // Handed on unaltered. The real write service normalizes a null to "" and this fake deliberately
        // does not, for the reason FakeMenuAdministration gives: what is under test is whether the
        // workflow passes the argument through, and a fake that trimmed would hide the one failure this
        // file can see.
        Assert.Equal("Served until 11am", described.LastDescription);
        Assert.IsType<MenuChanged>(Assert.Single(describedBroadcaster.Published));

        FakeMenuSectionAdministration unchanged = new()
        {
            DescribeOutcome = DescribeMenuSectionOutcome.NoChange,
        };
        RecordingBroadcaster unchangedBroadcaster = new();

        await WorkflowOver(unchanged, unchangedBroadcaster).DescribeMenuSectionAsync(
            MenuSectionIdentifier, "Served until 11am", ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(unchangedBroadcaster.Published);
    }

    /// <summary>
    /// A section move that committed is announced, because §11.1 renders the headings in
    /// <c>(display_order, name, identifier)</c> — so the whole guest menu is in a different order even
    /// though no item moved at all.
    /// </summary>
    [Fact]
    public async Task ASectionMove_IsAnnouncedOnlyWhenThePositionActuallyMoved()
    {
        FakeMenuSectionAdministration moved = new()
        {
            ReorderOutcome = ReorderMenuSectionOutcome.Reordered,
        };
        RecordingBroadcaster movedBroadcaster = new();

        Assert.Equal(
            ReorderMenuSectionOutcome.Reordered,
            await WorkflowOver(moved, movedBroadcaster).ReorderMenuSectionAsync(
                MenuSectionIdentifier, 2, ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal(2, moved.LastDisplayOrder);
        Assert.IsType<MenuChanged>(Assert.Single(movedBroadcaster.Published));

        FakeMenuSectionAdministration unchanged = new()
        {
            ReorderOutcome = ReorderMenuSectionOutcome.NoChange,
        };
        RecordingBroadcaster unchangedBroadcaster = new();

        await WorkflowOver(unchanged, unchangedBroadcaster).ReorderMenuSectionAsync(
            MenuSectionIdentifier, 2, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(unchangedBroadcaster.Published);
    }

    /// <summary>
    /// The loudest of the five, and the one whose broadcast is not optional.
    ///
    /// <para>§7 hides an inactive section from the guest <b>entirely</b> — the opposite of the rule one
    /// paragraph away for an inactive item, which stays visible and marked. So this flip adds or removes a
    /// whole part of the menu from every open picker, and a switch-off that announced nothing would leave
    /// those items tappable on every phone already looking at them until the send was refused server-side
    /// for a reason the guest never saw coming (§6.5.9). Both directions are asserted, because "announce
    /// the removal and not the restoration" is a plausible half-implementation that leaves a menu missing
    /// a heading it should have.</para>
    /// </summary>
    [Fact]
    public async Task ASectionVisibilityFlip_IsAnnouncedOnlyWhenTheFlagActuallyMoved()
    {
        foreach (bool target in new[] { false, true })
        {
            FakeMenuSectionAdministration changed = new()
            {
                ActivationOutcome = MenuSectionActivationOutcome.Changed,
            };
            RecordingBroadcaster changedBroadcaster = new();

            Assert.Equal(
                MenuSectionActivationOutcome.Changed,
                await WorkflowOver(changed, changedBroadcaster).SetMenuSectionActiveAsync(
                    MenuSectionIdentifier,
                    target,
                    ActorIdentifier,
                    TestContext.Current.CancellationToken));

            Assert.Equal(target, changed.LastIsActive);
            Assert.IsType<MenuChanged>(Assert.Single(changedBroadcaster.Published));
        }

        FakeMenuSectionAdministration unchanged = new()
        {
            ActivationOutcome = MenuSectionActivationOutcome.NoChange,
        };
        RecordingBroadcaster unchangedBroadcaster = new();

        await WorkflowOver(unchanged, unchangedBroadcaster).SetMenuSectionActiveAsync(
            MenuSectionIdentifier, isActive: false, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(unchangedBroadcaster.Published);
    }

    /// <summary>
    /// The sixth section verb, and the pair every one of them gets: the ordering reaches the write service
    /// exactly as the surface built it, and a committed resequence announces once.
    ///
    /// <para><b>Once, not once per row.</b> Moving one heading in eight writes two rows and two events, and
    /// <c>MenuChanged</c> means "re-read the menu" and nothing else (§9) — so a workflow that published per
    /// written row would tell every open phone in the building to re-query twice for one decision.</para>
    ///
    /// <para>The list is asserted by identity as well as by contents. Nothing in this verb's contract
    /// permits the workflow to sort, de-duplicate or re-order what it was given: the whole ordering <em>is</em>
    /// the argument, and a shell that improved it would be deciding the menu's order in the one layer that
    /// has no business having an opinion about it.</para>
    /// </summary>
    [Fact]
    public async Task AResequence_HandsTheWholeOrderingThroughAndAnnouncesOnce()
    {
        Guid[] ordering =
        [
            MenuSectionIdentifier,
            Guid.Parse("0192f000-0000-7000-8000-00000000d004"),
            Guid.Parse("0192f000-0000-7000-8000-00000000d005"),
        ];

        FakeMenuSectionAdministration sections = new();
        RecordingBroadcaster broadcaster = new();

        ResequenceMenuSectionsOutcome outcome = await WorkflowOver(sections, broadcaster)
            .ResequenceMenuSectionsAsync(ordering, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(ResequenceMenuSectionsOutcome.Resequenced, outcome);
        Assert.Same(ordering, sections.LastOrdering);
        Assert.Equal(ActorIdentifier, sections.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

    /// <summary>
    /// Two ways for a resequence to write nothing, and neither is announced.
    ///
    /// <para><c>NoChange</c> is the order it already had — an administrator pressing Up on a heading
    /// somebody else moved up a second earlier. <c>MenuSectionSetChanged</c> is a page rendered before the
    /// menu's set of headings changed, which the write service refuses whole rather than partially obeying.
    /// The distinction matters to the surface, which reports them differently; it does not matter here,
    /// because the rule this file exists to hold is about commits and both of these committed nothing.</para>
    /// </summary>
    [Fact]
    public async Task AResequenceThatWroteNothing_AnnouncesNothing()
    {
        foreach (ResequenceMenuSectionsOutcome quiet in new[]
        {
            ResequenceMenuSectionsOutcome.NoChange,
            ResequenceMenuSectionsOutcome.MenuSectionSetChanged,
        })
        {
            FakeMenuSectionAdministration sections = new() { ResequenceOutcome = quiet };
            RecordingBroadcaster broadcaster = new();

            Assert.Equal(
                quiet,
                await WorkflowOver(sections, broadcaster).ResequenceMenuSectionsAsync(
                    [MenuSectionIdentifier], ActorIdentifier, TestContext.Current.CancellationToken));

            Assert.Empty(broadcaster.Published);
        }
    }

    /// <summary>
    /// A stale editor, or a link somebody kept. Nothing was written by any of the four verbs, so nothing
    /// may be announced by any of them — and the surface above turns each into a redirect back to the menu
    /// rather than a silent success.
    /// </summary>
    [Fact]
    public async Task AnUnknownSection_AnnouncesNothing()
    {
        FakeMenuSectionAdministration sections = new()
        {
            RenameOutcome = RenameMenuSectionOutcome.MenuSectionNotFound,
            DescribeOutcome = DescribeMenuSectionOutcome.MenuSectionNotFound,
            ReorderOutcome = ReorderMenuSectionOutcome.MenuSectionNotFound,
            ActivationOutcome = MenuSectionActivationOutcome.MenuSectionNotFound,
        };
        RecordingBroadcaster broadcaster = new();
        IMenuWorkflow workflow = WorkflowOver(sections, broadcaster);

        await workflow.RenameMenuSectionAsync(
            MenuSectionIdentifier, "Puddings", ActorIdentifier, TestContext.Current.CancellationToken);
        await workflow.DescribeMenuSectionAsync(
            MenuSectionIdentifier, "Sweet things", ActorIdentifier, TestContext.Current.CancellationToken);
        await workflow.ReorderMenuSectionAsync(
            MenuSectionIdentifier, 2, ActorIdentifier, TestContext.Current.CancellationToken);
        await workflow.SetMenuSectionActiveAsync(
            MenuSectionIdentifier, isActive: false, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Empty(broadcaster.Published);
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

    /// <summary>
    /// The last verb of the menu enhancement to acquire a caller, and the loudest of the item verbs.
    ///
    /// <para>§11.1 groups the guest menu by heading, so a committed refile moves a card out of one
    /// grouping and into another on every open picker in the building — and if the destination is an
    /// inactive heading the card leaves the guest's menu <b>entirely</b>, because §7 renders no such
    /// heading at all. That is the same reach a section visibility flip has, from the other direction.</para>
    ///
    /// <para>All three silences are asserted rather than one. <c>NoChange</c> is the ordinary case — the
    /// picker on <c>ManageMenuItem</c> opens pre-selected on the item's own heading, so submitting it
    /// untouched is the single most likely call this verb ever receives — and <c>MenuSectionNotFound</c>
    /// is the arm a workflow keying on "not NoChange" would announce, for a write the database rolled
    /// back.</para>
    /// </summary>
    [Fact]
    public async Task ARefileBetweenSections_IsAnnouncedOnlyWhenItCommitted()
    {
        FakeMenuAdministration moved = new() { MoveOutcome = MoveMenuItemToSectionOutcome.Moved };
        RecordingBroadcaster movedBroadcaster = new();

        Assert.Equal(
            MoveMenuItemToSectionOutcome.Moved,
            await WorkflowOver(moved, movedBroadcaster).MoveMenuItemToSectionAsync(
                MenuItemIdentifier,
                MenuSectionIdentifier,
                ActorIdentifier,
                TestContext.Current.CancellationToken));

        Assert.Equal(MenuItemIdentifier, moved.LastMenuItemIdentifier);
        Assert.Equal(MenuSectionIdentifier, moved.LastMenuSectionIdentifier);
        Assert.Equal(ActorIdentifier, moved.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(movedBroadcaster.Published));

        foreach (MoveMenuItemToSectionOutcome silent in new[]
        {
            MoveMenuItemToSectionOutcome.NoChange,
            MoveMenuItemToSectionOutcome.MenuItemNotFound,
            MoveMenuItemToSectionOutcome.MenuSectionNotFound,
        })
        {
            FakeMenuAdministration administration = new() { MoveOutcome = silent };
            RecordingBroadcaster broadcaster = new();

            await WorkflowOver(administration, broadcaster).MoveMenuItemToSectionAsync(
                MenuItemIdentifier,
                MenuSectionIdentifier,
                ActorIdentifier,
                TestContext.Current.CancellationToken);

            Assert.Empty(broadcaster.Published);
        }
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

        /// <summary>
        /// The ordering the workflow handed through, recorded as its own list rather than folded into
        /// <see cref="LastMenuSectionIdentifier"/>: the whole claim about this verb is that the sequence
        /// arrives unaltered, and a fake that kept only the last element could not say so.
        /// </summary>
        public IReadOnlyList<Guid>? LastOrdering { get; private set; }

        public decimal? LastPriceAmount { get; private set; }

        public int? LastDisplayOrder { get; private set; }

        public Guid? LastActor { get; private set; }

        public RenameMenuItemResult RenameResult { get; init; } = new(
            RenameMenuItemOutcome.Renamed, MenuItemIdentifier, "Broth", "Soup");

        public RepriceMenuItemResult RepriceResult { get; init; } = new(
            RepriceMenuItemOutcome.Repriced, MenuItemIdentifier, "Soup", 5.00m, 4.50m);

        public DescribeMenuItemOutcome DescribeOutcome { get; init; } = DescribeMenuItemOutcome.Described;

        public ReorderMenuItemOutcome ReorderOutcome { get; init; } = ReorderMenuItemOutcome.Reordered;

        public MoveMenuItemToSectionOutcome MoveOutcome { get; init; }
            = MoveMenuItemToSectionOutcome.Moved;

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

        public Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
            Guid menuItemIdentifier,
            Guid menuSectionIdentifier,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(MoveOutcome);
        }
    }

    /// <summary>
    /// The section write service, of which the workflow now uses all five verbs.
    ///
    /// <para><b>Four of these threw until this slice, and the throw was load-bearing rather than
    /// laziness.</b> This fake is reachable from every test in the file through the default overloads, so
    /// a verb that quietly answered would have let a workflow start calling it with nothing here
    /// noticing — which is precisely the state a workflow verb with no caller is in. The throws are gone
    /// because the state they guarded is gone: <c>ManageMenuSection.razor</c> calls all four, each one
    /// publishes on a committed row, and each has a fact above.</para>
    ///
    /// <para>Each verb records its arguments and returns a configurable outcome, because both halves of
    /// this file's rule need saying: that the workflow hands the argument through unaltered, and that it
    /// announces exactly the outcomes that wrote something.</para>
    /// </summary>
    private sealed class FakeMenuSectionAdministration : IMenuSectionAdministration
    {
        public Guid? LastMenuSectionIdentifier { get; private set; }

        public string? LastName { get; private set; }

        public string? LastDescription { get; private set; }

        public int? LastDisplayOrder { get; private set; }

        public bool? LastIsActive { get; private set; }

        public Guid? LastActor { get; private set; }

        /// <summary>
        /// The ordering the workflow handed through, recorded as its own list rather than folded into
        /// <see cref="LastMenuSectionIdentifier"/>: the whole claim about this verb is that the sequence
        /// arrives unaltered, and a fake that kept only the last element could not say so.
        /// </summary>
        public IReadOnlyList<Guid>? LastOrdering { get; private set; }

        public CreateMenuSectionResult CreateResult { get; init; } = new(
            CreateMenuSectionOutcome.Created, MenuSectionIdentifier, "Drinks", "Cold things", 0);

        public RenameMenuSectionOutcome RenameOutcome { get; init; } = RenameMenuSectionOutcome.Renamed;

        public DescribeMenuSectionOutcome DescribeOutcome { get; init; }
            = DescribeMenuSectionOutcome.Described;

        public ReorderMenuSectionOutcome ReorderOutcome { get; init; } = ReorderMenuSectionOutcome.Reordered;

        public ResequenceMenuSectionsOutcome ResequenceOutcome { get; init; }
            = ResequenceMenuSectionsOutcome.Resequenced;

        public MenuSectionActivationOutcome ActivationOutcome { get; init; }
            = MenuSectionActivationOutcome.Changed;

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
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastName = name;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(RenameOutcome);
        }

        public Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
            Guid menuSectionIdentifier,
            string? description,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastDescription = description;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(DescribeOutcome);
        }

        public Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
            Guid menuSectionIdentifier,
            int displayOrder,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastDisplayOrder = displayOrder;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(ReorderOutcome);
        }

        public Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
            IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastOrdering = orderedMenuSectionIdentifiers;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(ResequenceOutcome);
        }

        public Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
            Guid menuSectionIdentifier,
            bool isActive,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastIsActive = isActive;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(ActivationOutcome);
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
