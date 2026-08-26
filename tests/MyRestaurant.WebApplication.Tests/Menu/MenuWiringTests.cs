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

        Assert.IsType<DapperMenuItemImageAdministration>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageAdministration>());
        Assert.IsType<DapperMenuItemImageDirectory>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageDirectory>());
    }

    [Fact]
    public void MenuItemPictureHistory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuItemImageEventLog>(
            scope.ServiceProvider.GetRequiredService<IMenuItemImageEventLog>());
    }

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

        Assert.Equal("Soup", result.Name);
        Assert.Equal("Lentil, vegan", result.Description);
        Assert.Equal(4.50m, result.PriceAmount);
        Assert.Equal(MenuSectionIdentifier, result.MenuSectionIdentifier);

        Assert.IsType<MenuChanged>(Assert.Single(broadcaster.Published));
    }

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

            Assert.Null(result.MenuItemImageIdentifier);
            Assert.Empty(broadcaster.Published);
        }

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

    private sealed class FakeMenuSectionAdministration : IMenuSectionAdministration
    {
        public Guid? LastMenuSectionIdentifier { get; private set; }

        public string? LastName { get; private set; }

        public string? LastDescription { get; private set; }

        public int? LastDisplayOrder { get; private set; }

        public bool? LastIsActive { get; private set; }

        public Guid? LastActor { get; private set; }

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
            }
        }
    }
}
