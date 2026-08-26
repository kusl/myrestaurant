using MyRestaurant.DataAccess.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

public sealed class MenuGroupingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Drinks = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Puddings = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Breakfast = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void AnEmptyMenuYieldsNoHeadingsThroughEitherDoor()
    {
        Assert.Empty(MenuGrouping.VisibleToGuests([]));
        Assert.Empty(MenuGrouping.EveryHeading([]));
    }

    [Fact]
    public void ContiguousItemsBecomeOneHeadingEachInTheOrderTheyArrived()
    {
        IReadOnlyList<MenuHeadingGroup> headings = MenuGrouping.VisibleToGuests(
        [
            Item(Drinks, "Drinks", "Cola"),
            Item(Drinks, "Drinks", "Tea"),
            Item(Puddings, "Puddings", "Trifle"),
        ]);

        Assert.Equal(2, headings.Count);

        Assert.Equal("Drinks", headings[0].MenuSectionName);
        Assert.Equal(["Cola", "Tea"], headings[0].Items.Select(item => item.Name));

        Assert.Equal("Puddings", headings[1].MenuSectionName);
        Assert.Equal("Trifle", Assert.Single(headings[1].Items).Name);
    }

    [Fact]
    public void AHeadingHiddenFromGuestsIsAbsentForThemAndPresentForTheKitchen()
    {
        MenuItemSummary[] menu =
        [
            Item(Drinks, "Drinks", "Cola"),
            Item(Breakfast, "Breakfast", "Eggs", menuSectionIsActive: false),
            Item(Breakfast, "Breakfast", "Toast", menuSectionIsActive: false),
            Item(Puddings, "Puddings", "Trifle"),
        ];

        IReadOnlyList<MenuHeadingGroup> guest = MenuGrouping.VisibleToGuests(menu);
        IReadOnlyList<MenuHeadingGroup> kitchen = MenuGrouping.EveryHeading(menu);

        Assert.Equal(["Drinks", "Puddings"], guest.Select(heading => heading.MenuSectionName));
        Assert.Equal(["Drinks", "Breakfast", "Puddings"], kitchen.Select(heading => heading.MenuSectionName));

        Assert.Equal(
            [Breakfast],
            kitchen.Select(heading => heading.MenuSectionIdentifier)
                .Except(guest.Select(heading => heading.MenuSectionIdentifier)));

        Assert.False(kitchen.Single(heading => heading.MenuSectionIdentifier == Breakfast).MenuSectionIsActive);
        Assert.All(guest, heading => Assert.True(heading.MenuSectionIsActive));

        Assert.Equal(
            ["Eggs", "Toast"],
            kitchen.Single(heading => heading.MenuSectionIdentifier == Breakfast).Items.Select(item => item.Name));
    }

    [Fact]
    public void AHiddenHeadingAtTheHeadOfTheListDoesNotSwallowTheHeadingAfterIt()
    {
        IReadOnlyList<MenuHeadingGroup> headings = MenuGrouping.VisibleToGuests(
        [
            Item(Breakfast, "Breakfast", "Eggs", menuSectionIsActive: false),
            Item(Drinks, "Drinks", "Cola"),
        ]);

        MenuHeadingGroup only = Assert.Single(headings);
        Assert.Equal(Drinks, only.MenuSectionIdentifier);
        Assert.Equal("Cola", Assert.Single(only.Items).Name);
    }

    [Fact]
    public void AMenuWithEveryHeadingSwitchedOffIsEmptyForGuestsAndWholeForTheKitchen()
    {
        MenuItemSummary[] menu =
        [
            Item(Breakfast, "Breakfast", "Eggs", menuSectionIsActive: false),
            Item(Puddings, "Puddings", "Trifle", menuSectionIsActive: false),
        ];

        Assert.Empty(MenuGrouping.VisibleToGuests(menu));
        Assert.Equal(2, MenuGrouping.EveryHeading(menu).Count);
    }

    [Fact]
    public void AnItemThatIsEightySixedStaysUnderItsHeading()
    {
        MenuHeadingGroup only = Assert.Single(MenuGrouping.VisibleToGuests(
        [
            Item(Drinks, "Drinks", "Cola"),
            Item(Drinks, "Drinks", "Tea", isActive: false),
        ]));

        Assert.Equal(["Cola", "Tea"], only.Items.Select(item => item.Name));
        Assert.False(only.Items[1].IsActive);
    }

    [Fact]
    public void AHeadingsNameAndDescriptionAreReadFromTheFirstRowOfItsRun()
    {
        MenuHeadingGroup only = Assert.Single(MenuGrouping.EveryHeading(
        [
            Item(Drinks, "Drinks", "Cola", menuSectionDescription: "Served all day"),
            Item(Drinks, "SOMETHING ELSE", "Tea", menuSectionDescription: "A LATER SENTENCE"),
        ]));

        Assert.Equal("Drinks", only.MenuSectionName);
        Assert.Equal("Served all day", only.MenuSectionDescription);
    }

    [Fact]
    public void AHeadingWithNoDescriptionCarriesTheEmptyStringRatherThanNull()
    {
        MenuHeadingGroup only = Assert.Single(MenuGrouping.VisibleToGuests(
            [Item(Drinks, "Drinks", "Cola")]));

        Assert.Equal(string.Empty, only.MenuSectionDescription);
    }

    [Fact]
    public void TheWalkPreservesTheOrderItIsHandedRatherThanSortingAnything()
    {
        IReadOnlyList<MenuHeadingGroup> headings = MenuGrouping.EveryHeading(
        [
            Item(Puddings, "Puddings", "Trifle"),
            Item(Puddings, "Puddings", "Sorbet"),
            Item(Drinks, "Drinks", "Tea"),
            Item(Drinks, "Drinks", "Cola"),
        ]);

        Assert.Equal([Puddings, Drinks], headings.Select(heading => heading.MenuSectionIdentifier));
        Assert.Equal(["Trifle", "Sorbet"], headings[0].Items.Select(item => item.Name));
        Assert.Equal(["Tea", "Cola"], headings[1].Items.Select(item => item.Name));
    }

    [Fact]
    public void ANonContiguousListYieldsOneGroupPerRunRatherThanOnePerHeading()
    {
        IReadOnlyList<MenuHeadingGroup> headings = MenuGrouping.EveryHeading(
        [
            Item(Drinks, "Drinks", "Cola"),
            Item(Puddings, "Puddings", "Trifle"),
            Item(Drinks, "Drinks", "Tea"),
        ]);

        Assert.Equal(3, headings.Count);
        Assert.Equal([Drinks, Puddings, Drinks], headings.Select(heading => heading.MenuSectionIdentifier));
    }

    [Fact]
    public void NeitherDoorAcceptsANullList()
    {
        Assert.Throws<ArgumentNullException>(() => MenuGrouping.VisibleToGuests(null!));
        Assert.Throws<ArgumentNullException>(() => MenuGrouping.EveryHeading(null!));
    }

    private static MenuItemSummary Item(
        Guid menuSectionIdentifier,
        string menuSectionName,
        string name,
        string menuSectionDescription = "",
        bool menuSectionIsActive = true,
        bool isActive = true)
        => new(
            Guid.NewGuid(),
            menuSectionIdentifier,
            menuSectionName,
            menuSectionDescription,
            menuSectionIsActive,
            name,
            string.Empty,
            9.50m,
            0,
            isActive,
            Noon);
}
