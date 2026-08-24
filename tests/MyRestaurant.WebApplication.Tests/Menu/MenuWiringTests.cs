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
/// <para><b>The two picture verbs arrive the same way and close the obligation Slice 51 re-opened by
/// name.</b> <c>0006</c> shipped <see cref="IMenuItemImageAdministration"/> with no caller outside its
/// integration tests and said so; it was the weaker form of the defect, because nothing was added behind
/// <see cref="IMenuWorkflow"/> and therefore no surface could change a picture without announcing it for
/// the reason that no surface could change one at all. Both arrive with the form on
/// <c>ManageMenuItem.razor</c>, and the fact worth reading is the refusal set: this is the verb with the
/// most ways to write nothing in the file, five of them, three of which never open a transaction.</para>
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
    /// One workflow over every write service <em>that changes the menu</em>. §9 does not distinguish which
    /// verb changed it, and every subscriber responds to <see cref="MenuChanged"/> the same way, so a
    /// second workflow would only make it possible to wire an application that announces 86s and not
    /// repricings.
    ///
    /// <para><b>That qualifier arrived with Stage 5a and it narrows the sentence rather than weakening
    /// it.</b> <see cref="IMenuItemReactions"/> is a write and is deliberately not behind the workflow:
    /// a like moves no name, no price, no heading, no position, no availability flag and no photograph,
    /// so no surface has anything to re-read, and it is the one write in this application that can fire
    /// many times a minute at one table. What this fact claims is what it always meant — that a write
    /// which changes what a guest's picker renders cannot be reached without going through the thing
    /// that announces it.</para>
    /// </summary>
    [Fact]
    public void MenuWorkflow_IsResolvableInAScope_AndCoversEveryWriteServiceThatChangesTheMenu()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<MenuWorkflow>(scope.ServiceProvider.GetRequiredService<IMenuWorkflow>());
        Assert.IsType<DapperMenuAvailability>(scope.ServiceProvider.GetRequiredService<IMenuAvailability>());
        Assert.IsType<DapperMenuAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuAdministration>());
        Assert.IsType<DapperMenuSectionAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuSectionAdministration>());

        // The fourth, as of Stage 4b. Asserted here rather than in a fact of its own because the claim
        // this one makes is that the workflow covers EVERY write service — a fourth registered beside it
        // and not behind it is exactly the shape the picture services were in for one slice.
        Assert.IsType<DapperMenuItemImageAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageAdministration>());
        Assert.IsType<DapperMenuItemImageDirectory>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageDirectory>());
    }

    /// <summary>
    /// The picture history reader, resolvable in a scope — the third event log this composition registers,
    /// after <see cref="IMenuEventLog"/> and <see cref="IMenuSectionEventLog"/>.
    ///
    /// <para><b>A fact of its own rather than a line in the one above, and the reason is what each fact
    /// claims.</b> That one asserts that <see cref="IMenuWorkflow"/> covers every <em>write</em> service, so
    /// a read added to its body would weaken the sentence it exists to make. This is a read and it is taken
    /// straight by the surface that renders it, exactly as <see cref="IMenuDirectory"/> and
    /// <see cref="IMenuSectionDirectory"/> are.</para>
    ///
    /// <para><b>What makes it worth a registration fact at all is the failure mode.</b>
    /// <c>ManageMenuItem.razor</c> is static SSR and resolves this by constructor injection during
    /// rendering, so an unregistered service is not a compile error and not a test failure — it is an
    /// exception on §11.4's item page, which is one of the ten surfaces §16.3 scenario 16 visits and
    /// therefore a red suite, but only after Chromium and a database have started. Two seconds here instead
    /// of two and a half minutes there is the whole argument for every fact in this class.</para>
    /// </summary>
    [Fact]
    public void MenuItemPictureHistory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuItemImageEventLog>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageEventLog>());
    }

    /// <summary>
    /// The two reaction services, resolvable in a scope (Stage 5a).
    ///
    /// <para><b>A fact of its own, and for the opposite reason the picture history has one.</b> That read
    /// is outside the workflow's fact because it is a <em>read</em>. These two are outside it because one
    /// of them is a <b>write that is deliberately not behind the workflow</b> — so putting
    /// <see cref="IMenuItemReactions"/> into the body above would make that fact assert the negation of
    /// what its own name says. Keeping them apart is what lets both sentences stay true.</para>
    ///
    /// <para><b>Worth asserting although nothing resolves either service yet.</b> Stage 5a registers them
    /// and Stage 5b builds the surfaces, and the failure mode in between is the one this whole class
    /// exists for: §11.1's picker and §11.4's item page are rendered by components that resolve their
    /// dependencies while rendering, so an unregistered service is not a compile error and not a unit
    /// failure — it is an exception on a live surface, found by Chromium and a database two and a half
    /// minutes later. Two seconds here instead.</para>
    /// </summary>
    [Fact]
    public void MenuItemReactionServices_AreResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuItemReactionDirectory>(
            scope.ServiceProvider.GetRequiredService<IMenuItemReactionDirectory>());
        Assert.IsType<DapperMenuItemReactions>(
            scope.ServiceProvider.GetRequiredService<IMenuItemReactions>());
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
    /// <para><b>This publish reached no guest surface for nine slices, and the bet it was making has now
    /// paid.</b> The argument was that <c>MenuChanged</c> means "re-read the menu" and nothing else, so a
    /// workflow deciding which columns were worth announcing would have to be edited the moment a surface
    /// started reading one. Slice 49 is that moment — §11.1 renders a heading's description under its
    /// heading now — and neither the workflow nor this fact needed changing. A tree where this publish had
    /// been conditional would have shipped a guest menu that showed the new sentence to whoever reloaded
    /// and the old one to everybody already looking at it.</para>
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
    /// One resequence, one announcement, and the heading and the ordering both arrive unaltered.
    ///
    /// <para><b>One publish for the whole call however many rows it wrote.</b> The same rule the section
    /// resequence carries and for the same reason: <c>MenuChanged</c> means "re-read the menu" and nothing
    /// else (§9), so a workflow publishing per written row would tell every open phone in the building to
    /// re-query several times over one decision.</para>
    ///
    /// <para><b>The heading is asserted alongside the list, which is the fact this verb has and the section
    /// one does not.</b> The set being reordered is one heading's items, so the workflow forwards two things
    /// that must stay together — a shell that dropped the heading would leave the write service reordering
    /// whichever heading it felt like. The list is asserted by identity as well as by contents: nothing in
    /// this verb's contract permits the workflow to sort, de-duplicate or re-order what it was given, since
    /// the whole ordering <em>is</em> the argument.</para>
    /// </summary>
    [Fact]
    public async Task AnItemResequence_HandsTheHeadingAndTheOrderingThroughAndAnnouncesOnce()
    {
        Guid[] ordering =
        [
            MenuItemIdentifier,
            Guid.Parse("0192f000-0000-7000-8000-00000000e001"),
            Guid.Parse("0192f000-0000-7000-8000-00000000e002"),
        ];

        FakeMenuAdministration administration = new();
        RecordingBroadcaster broadcaster = new();

        ResequenceMenuItemsOutcome outcome = await WorkflowOver(administration, broadcaster)
            .ResequenceMenuItemsAsync(
                MenuSectionIdentifier, ordering, ActorIdentifier, TestContext.Current.CancellationToken);

        Assert.Equal(ResequenceMenuItemsOutcome.Resequenced, outcome);
        Assert.Equal(MenuSectionIdentifier, administration.LastMenuSectionIdentifier);
        Assert.Same(ordering, administration.LastOrdering);
        Assert.Equal(ActorIdentifier, administration.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

    /// <summary>
    /// Two ways for an item resequence to write nothing, and neither is announced.
    ///
    /// <para><c>NoChange</c> is the order it already had. <c>MenuItemSetChanged</c> is a page rendered
    /// before the heading's items changed — or one naming a heading this menu does not hold, which reaches
    /// the same outcome because an unknown heading has no items under it. The distinction matters to the
    /// surface, which words them differently; it does not matter here, because the rule this file exists to
    /// hold is about commits and neither of these committed anything.</para>
    /// </summary>
    [Fact]
    public async Task AnItemResequenceThatWroteNothing_AnnouncesNothing()
    {
        foreach (ResequenceMenuItemsOutcome quiet in new[]
        {
            ResequenceMenuItemsOutcome.NoChange,
            ResequenceMenuItemsOutcome.MenuItemSetChanged,
        })
        {
            FakeMenuAdministration administration = new() { ResequenceOutcome = quiet };
            RecordingBroadcaster broadcaster = new();

            Assert.Equal(
                quiet,
                await WorkflowOver(administration, broadcaster).ResequenceMenuItemsAsync(
                    MenuSectionIdentifier,
                    [MenuItemIdentifier],
                    ActorIdentifier,
                    TestContext.Current.CancellationToken));

            Assert.Empty(broadcaster.Published);
        }
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

    /// <summary>
    /// The picture arrives at the write service exactly as the form built it, and a stored one is
    /// announced.
    ///
    /// <para><b>The bytes are asserted by identity rather than by contents</b>, which is the same claim
    /// the two resequencing facts make about their orderings and it matters more here: nothing in this
    /// verb's contract permits the workflow to copy, trim, pad or re-encode an upload, and a shell that
    /// "helpfully" normalised one would be deciding what a photograph is in the one layer with no
    /// business having an opinion. The declared media type is asserted for the same reason — it is the
    /// browser's claim, and the write's whole job is to check it against these very bytes.</para>
    ///
    /// <para><b>The identifier is asserted because a replace mints a new one.</b> §7's route is keyed on
    /// the image so that an immutable cache header is true; a workflow that passed the item's identifier
    /// through instead would produce a URL that never changes and a year of stale photographs.</para>
    /// </summary>
    [Fact]
    public async Task AnAttachedPicture_IsAnnounced_AndItsArgumentsArePassedThrough()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
        Guid imageIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000f001");

        FakeMenuItemImageAdministration images = new();
        RecordingBroadcaster broadcaster = new();

        AttachMenuItemImageResult result = await WorkflowOver(images, broadcaster)
            .AttachMenuItemImageAsync(
                imageIdentifier,
                MenuItemIdentifier,
                "image/png",
                bytes,
                ActorIdentifier,
                TestContext.Current.CancellationToken);

        Assert.Equal(AttachMenuItemImageOutcome.Attached, result.Outcome);
        Assert.Equal(imageIdentifier, images.LastMenuItemImageIdentifier);
        Assert.Equal(MenuItemIdentifier, images.LastMenuItemIdentifier);
        Assert.Equal("image/png", images.LastContentType);
        Assert.Same(bytes, images.LastBytes);
        Assert.Equal(ActorIdentifier, images.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

    /// <summary>
    /// Every refusal is silent, and they are asserted as a set rather than one at a time because the set
    /// is the claim.
    ///
    /// <para>This is the verb with the most ways to write nothing in the whole file, and three of them
    /// never open a transaction at all — an empty upload, a media type this application does not serve,
    /// and bytes that contradict their own declaration are properties of the arguments. A workflow keyed
    /// on "not a refusal I have heard of" would announce a future member of that enum by default, which
    /// is why the implementation keys on the two outcomes that wrote a row instead. <b>A replace is
    /// announced and is deliberately not in this list</b>: it deleted one row and wrote another, so it
    /// changed the menu more than an attach did.</para>
    /// </summary>
    [Fact]
    public async Task APictureThatWasNotStored_AnnouncesNothing()
    {
        foreach (AttachMenuItemImageOutcome refused in new[]
        {
            AttachMenuItemImageOutcome.MenuItemNotFound,
            AttachMenuItemImageOutcome.UnsupportedContentType,
            AttachMenuItemImageOutcome.ContentTypeContradictedByBytes,
            AttachMenuItemImageOutcome.BytesEmpty,
            AttachMenuItemImageOutcome.BytesOverCap,
        })
        {
            FakeMenuItemImageAdministration images = new() { AttachOutcome = refused };
            RecordingBroadcaster broadcaster = new();

            AttachMenuItemImageResult result = await WorkflowOver(images, broadcaster)
                .AttachMenuItemImageAsync(
                    Guid.Parse("0192f000-0000-7000-8000-00000000f002"),
                    MenuItemIdentifier,
                    "image/png",
                    [0x00],
                    ActorIdentifier,
                    TestContext.Current.CancellationToken);

            Assert.Equal(refused, result.Outcome);

            // Nothing was stored, so nothing may claim to have been: a caller that built a URL out of
            // the identifier it offered would link to a 404 on every card.
            Assert.Null(result.MenuItemImageIdentifier);
            Assert.Empty(broadcaster.Published);
        }

        // The replace half of the same rule, stated positively so the loop above cannot be read as
        // "anything but Attached is silent".
        FakeMenuItemImageAdministration replaced = new()
        {
            AttachOutcome = AttachMenuItemImageOutcome.Replaced,
        };
        RecordingBroadcaster replacedBroadcaster = new();

        await WorkflowOver(replaced, replacedBroadcaster).AttachMenuItemImageAsync(
            Guid.Parse("0192f000-0000-7000-8000-00000000f003"),
            MenuItemIdentifier,
            "image/png",
            [0x00],
            ActorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.IsType<MenuChanged>(Assert.Single(replacedBroadcaster.Published));
    }

    /// <summary>
    /// A removal that deleted a row is announced; one that found nothing to delete, and one against an
    /// item that is not there, are not. The middle case is the ordinary one — two administrators pressing
    /// Remove seconds apart — and it is the reason this verb is conditional rather than unconditional.
    /// </summary>
    [Fact]
    public async Task ARemovedPicture_IsAnnouncedOnlyWhenARowWasDeleted()
    {
        FakeMenuItemImageAdministration removed = new();
        RecordingBroadcaster removedBroadcaster = new();

        Assert.Equal(
            RemoveMenuItemImageOutcome.Removed,
            await WorkflowOver(removed, removedBroadcaster).RemoveMenuItemImageAsync(
                MenuItemIdentifier, ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal(MenuItemIdentifier, removed.LastMenuItemIdentifier);
        Assert.Equal(ActorIdentifier, removed.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(removedBroadcaster.Published));

        foreach (RemoveMenuItemImageOutcome quiet in new[]
        {
            RemoveMenuItemImageOutcome.NoImage,
            RemoveMenuItemImageOutcome.MenuItemNotFound,
        })
        {
            FakeMenuItemImageAdministration images = new() { RemoveOutcome = quiet };
            RecordingBroadcaster broadcaster = new();

            await WorkflowOver(images, broadcaster).RemoveMenuItemImageAsync(
                MenuItemIdentifier, ActorIdentifier, TestContext.Current.CancellationToken);

            Assert.Empty(broadcaster.Published);
        }
    }

    /// <summary>
    /// A caption that moved is announced; one that did not, and one with nothing to caption, are not.
    ///
    /// <para><b>The middle arm is the ordinary case rather than an edge case, and that is why it is
    /// asserted.</b> §11.4's caption form is pre-filled with what is stored, so the commonest submission of
    /// it is an unchanged caption — a workflow keyed on "the call returned" rather than on <c>Changed</c>
    /// would tell every open surface in the building to re-query the menu because somebody pressed a
    /// button. <c>NoImage</c> is two administrators seconds apart, one removing a photograph while the
    /// other types about it, and <c>MenuItemNotFound</c> is a page left open.</para>
    ///
    /// <para>The caption is asserted to arrive <b>verbatim</b>, on the same reading as the upload's bytes
    /// one fact up: nothing in this verb's contract permits the shell to trim, case-fold or normalise text
    /// somebody wrote, and here a trim would not merely tidy — <c>""</c> means no caption, so trimming a
    /// space is a clearing nobody asked for.</para>
    /// </summary>
    [Fact]
    public async Task ACaption_IsAnnouncedOnlyWhenItMoved_AndArrivesVerbatim()
    {
        const string Caption = "  Served on a bed of wilted greens  ";

        FakeMenuItemImageAdministration changed = new();
        RecordingBroadcaster changedBroadcaster = new();

        Assert.Equal(
            SetMenuItemImageAltTextOutcome.Changed,
            await WorkflowOver(changed, changedBroadcaster).SetMenuItemImageAltTextAsync(
                MenuItemIdentifier, Caption, ActorIdentifier, TestContext.Current.CancellationToken));

        Assert.Equal(MenuItemIdentifier, changed.LastMenuItemIdentifier);
        Assert.Equal(Caption, changed.LastAltText);
        Assert.Equal(ActorIdentifier, changed.LastActor);
        Assert.IsType<MenuChanged>(Assert.Single(changedBroadcaster.Published));

        foreach (SetMenuItemImageAltTextOutcome quiet in new[]
        {
            SetMenuItemImageAltTextOutcome.NoChange,
            SetMenuItemImageAltTextOutcome.NoImage,
            SetMenuItemImageAltTextOutcome.MenuItemNotFound,
        })
        {
            FakeMenuItemImageAdministration images = new() { AltTextOutcome = quiet };
            RecordingBroadcaster broadcaster = new();

            await WorkflowOver(images, broadcaster).SetMenuItemImageAltTextAsync(
                MenuItemIdentifier, Caption, ActorIdentifier, TestContext.Current.CancellationToken);

            Assert.Empty(broadcaster.Published);
        }
    }

    // Four overloads, distinguished by their first parameter: whichever write service the test is about
    // is the one it passes, and the others are default fakes nothing under test ever calls.
    private static MenuWorkflow WorkflowOver(
        IMenuAdministration administration,
        IDomainEventBroadcaster broadcaster)
        => new(
            new FakeMenuAvailability(),
            administration,
            new FakeMenuSectionAdministration(),
            new FakeMenuItemImageAdministration(),
            broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuAvailability availability,
        IDomainEventBroadcaster broadcaster)
        => new(
            availability,
            new FakeMenuAdministration(),
            new FakeMenuSectionAdministration(),
            new FakeMenuItemImageAdministration(),
            broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuSectionAdministration sections,
        IDomainEventBroadcaster broadcaster)
        => new(
            new FakeMenuAvailability(),
            new FakeMenuAdministration(),
            sections,
            new FakeMenuItemImageAdministration(),
            broadcaster);

    private static MenuWorkflow WorkflowOver(
        IMenuItemImageAdministration images,
        IDomainEventBroadcaster broadcaster)
        => new(
            new FakeMenuAvailability(),
            new FakeMenuAdministration(),
            new FakeMenuSectionAdministration(),
            images,
            broadcaster);

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

        public ResequenceMenuItemsOutcome ResequenceOutcome { get; init; }
            = ResequenceMenuItemsOutcome.Resequenced;

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

        public Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
            Guid menuSectionIdentifier,
            IReadOnlyList<Guid> orderedMenuItemIdentifiers,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuSectionIdentifier = menuSectionIdentifier;
            LastOrdering = orderedMenuItemIdentifiers;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(ResequenceOutcome);
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

    /// <summary>
    /// The picture write service, whose two verbs came behind the workflow in Stage 4b.
    ///
    /// <para>It records its arguments and returns a configurable outcome, like every other fake here,
    /// and the argument worth recording is <see cref="LastBytes"/> — kept as the reference it was handed
    /// rather than a copy, because the claim under test is that the upload arrives unaltered and a fake
    /// that copied could not tell an unaltered array from a re-encoded one.</para>
    /// </summary>
    private sealed class FakeMenuItemImageAdministration : IMenuItemImageAdministration
    {
        public Guid? LastMenuItemImageIdentifier { get; private set; }

        public Guid? LastMenuItemIdentifier { get; private set; }

        public string? LastContentType { get; private set; }

        public byte[]? LastBytes { get; private set; }

        public Guid? LastActor { get; private set; }

        public AttachMenuItemImageOutcome AttachOutcome { get; init; }
            = AttachMenuItemImageOutcome.Attached;

        public RemoveMenuItemImageOutcome RemoveOutcome { get; init; }
            = RemoveMenuItemImageOutcome.Removed;

        /// <summary>
        /// The caption the workflow handed through, recorded verbatim. <b>Not trimmed and not normalised
        /// by this fake</b>, because the claim under test is that the shell alters nothing — and a caption
        /// is the one argument in this file where a "helpful" trim would change meaning rather than
        /// tidy it: <c>""</c> means <em>no caption</em> (§7), so a shell that trimmed a single space would
        /// silently turn one operator's edit into a clearing.
        /// </summary>
        public string? LastAltText { get; private set; }

        public SetMenuItemImageAltTextOutcome AltTextOutcome { get; init; }
            = SetMenuItemImageAltTextOutcome.Changed;

        public Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
            Guid menuItemImageIdentifier,
            Guid menuItemIdentifier,
            string contentType,
            byte[] bytes,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemImageIdentifier = menuItemImageIdentifier;
            LastMenuItemIdentifier = menuItemIdentifier;
            LastContentType = contentType;
            LastBytes = bytes;
            LastActor = actorPersonIdentifier;

            // The real service returns the identifier only when it stored something, and this fake
            // reproduces that rather than always returning one: a caller that built a URL out of a
            // refused identifier would link to a 404, and the assertion for it needs a fake that can
            // be wrong in the same way the real thing could.
            bool stored = AttachOutcome is AttachMenuItemImageOutcome.Attached
                or AttachMenuItemImageOutcome.Replaced;

            return Task.FromResult(new AttachMenuItemImageResult(
                AttachOutcome,
                stored ? menuItemImageIdentifier : null));
        }

        public Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
            Guid menuItemIdentifier,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(RemoveOutcome);
        }

        public Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
            Guid menuItemIdentifier,
            string altText,
            Guid actorPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastMenuItemIdentifier = menuItemIdentifier;
            LastAltText = altText;
            LastActor = actorPersonIdentifier;

            return Task.FromResult(AltTextOutcome);
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
