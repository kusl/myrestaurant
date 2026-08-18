using MyRestaurant.DataAccess.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// Unit tests for <see cref="MenuGrouping"/> (TECHNICAL_SPECIFICATION §7, §11.1, §11.2).
///
/// <para>No container, no database and no component. That is the entire point of this file, and it is
/// <b>F-100</b>: the walk under test implements §11.1's grouping <em>and</em> §7's two opposite-pointing
/// visibility rules, and from M6 Slice 40 to Slice 50 it lived as a private property inside
/// <c>TableOrderSurface.razor</c>, where — this repository having no bUnit (§16.1) — the only thing that
/// could assert any of it was §16.3 scenario 17, needing a browser, a database and two and a half minutes.
/// <c>KitchenQueue</c>'s summary is the sentence that governs the case: a rule that can only be checked by
/// rendering a Razor component is a rule nobody checks.</para>
///
/// <para><b>The fact worth reading twice is
/// <see cref="AHeadingHiddenFromGuestsIsAbsentForThemAndPresentForTheKitchen"/>.</b> It is the only place
/// in this project where §7's asymmetry is asserted as an asymmetry — the same input through both doors,
/// with the two answers compared against each other. Asserting either door alone says very little: a
/// heading missing from the guest's answer has several possible reasons, and a heading present in the
/// kitchen's has none. It is the disagreement that is the test, which is the same shape §16.3 scenario 17's
/// closing step uses one register up.</para>
///
/// <para><b>What is deliberately not asserted:</b> that the ordering is right. The six-key ordering is
/// <c>IMenuDirectory</c>'s and is owned by integration tests against a real PostgreSQL, because it is SQL.
/// What is asserted here is that the walk <em>preserves</em> whatever order it is handed and re-decides
/// nothing — which is the property that would break the day somebody replaced the walk with a
/// <c>GroupBy</c>.</para>
/// </summary>
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

    /// <summary>
    /// The ordinary case: a run per heading, in the order the list arrived, with the items under each in
    /// the order they arrived.
    /// </summary>
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

    /// <summary>
    /// §7's asymmetry, asserted as an asymmetry: one input, both doors, and the answers compared.
    ///
    /// <para>Breakfast is switched off. The guest must not see it at all — switching off a heading is a
    /// decision about a whole part of the menu — and the kitchen must see it, because §7 says deactivating
    /// a heading does <b>not</b> deactivate its items and the "86" panel is the only surface that can read
    /// or change those flags. A cook who cannot reach the eggs cannot 86 them before breakfast comes
    /// back on.</para>
    ///
    /// <para>The two answers are compared rather than asserted separately, on the reasoning §16.3 scenario
    /// 17's closing step records: a heading missing from one list has several possible causes and a heading
    /// present in the other has none, so the fact lives in the difference. That difference must be exactly
    /// the hidden heading — an implementation that dropped it from <em>both</em> lists, or from neither,
    /// fails here rather than passing half of a rule.</para>
    /// </summary>
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

        // The difference is exactly the hidden heading, and it is stated as a set difference rather than
        // as two counts: two lists whose lengths differ by one can differ by one in the wrong place.
        Assert.Equal(
            [Breakfast],
            kitchen.Select(heading => heading.MenuSectionIdentifier)
                .Except(guest.Select(heading => heading.MenuSectionIdentifier)));

        // The flag is carried, so the kitchen can chip the heading without a second read; and the guest's
        // list can only hold active headings, which is what makes the member always true on that side.
        Assert.False(kitchen.Single(heading => heading.MenuSectionIdentifier == Breakfast).MenuSectionIsActive);
        Assert.All(guest, heading => Assert.True(heading.MenuSectionIsActive));

        // Both items under the hidden heading survive, in order — the non-cascade rule seen from the walk.
        Assert.Equal(
            ["Eggs", "Toast"],
            kitchen.Single(heading => heading.MenuSectionIdentifier == Breakfast).Items.Select(item => item.Name));
    }

    /// <summary>
    /// A hidden heading first in the list is dropped without swallowing the heading behind it.
    ///
    /// <para>This is the arrangement the flush guard exists for. The walk starts a new run when the
    /// identifier changes, guarded on the run being non-empty rather than on a sentinel identifier — so the
    /// first heading a guest is allowed to see may arrive after any number of filtered rows, and it must
    /// still open a run rather than be folded into an empty one.</para>
    /// </summary>
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

    /// <summary>
    /// A menu where every heading is switched off renders no menu to a guest and the whole menu to the
    /// kitchen — the degenerate end of the rule above, and the one an early <c>return</c> would get wrong.
    /// </summary>
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

    /// <summary>
    /// An <b>item</b> that is switched off stays in its heading's run, which is the opposite of the rule
    /// one register up and is the half §7 restates every time it mentions either: "the guest sees that the
    /// salmon exists and is out, rather than watching it silently vanish".
    ///
    /// <para>Asserted through the <em>guest's</em> door on purpose. That is the door with a filter in it,
    /// so it is the one where an over-eager condition — <c>item.IsActive</c> tested beside
    /// <c>item.MenuSectionIsActive</c>, which is a plausible single-character mistake — would remove the
    /// row. The kitchen's door has no filter and could not report it.</para>
    /// </summary>
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

    /// <summary>
    /// The heading's name and description come from the run, and the <b>first</b> row of it is where the
    /// walk reads them — which is the second half of F-100.
    ///
    /// <para>Before Slice 50 the walk assigned those two members inside the loop body on every iteration,
    /// so a group took them from the <em>last</em> row of its run while the summary above it said the
    /// first. Nothing in the application could ever have failed on that, because
    /// <see cref="MenuItemSummary"/> joins both columns from one <c>menu_section</c> row through an INNER
    /// JOIN and every row of a run therefore carries identical values. <b>This fact is the only place the
    /// two readings can be told apart</b>, and it can do it precisely because it is a unit test: it hands
    /// over rows the database could not produce, disagreeing about their own heading's name, and asserts
    /// which one won. A claim no test can falsify is either deleted or made true (F-77); this one is made
    /// true and then held.</para>
    ///
    /// <para>The arrangement is deliberately impossible rather than merely unusual, and that is stated
    /// because it is the kind of thing a later reader repairs by mistake. It is not asserting that the
    /// directory can return this; it is asserting which row the walk reads, and the only way to observe
    /// that is to make the rows differ.</para>
    /// </summary>
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

    /// <summary>
    /// A heading with no description carries <c>""</c> rather than null, so §11.1 tests
    /// <see cref="string.Length"/> and renders no paragraph rather than an empty one (§7 — <c>''</c> means
    /// <em>none</em>).
    /// </summary>
    [Fact]
    public void AHeadingWithNoDescriptionCarriesTheEmptyStringRatherThanNull()
    {
        MenuHeadingGroup only = Assert.Single(MenuGrouping.VisibleToGuests(
            [Item(Drinks, "Drinks", "Cola")]));

        Assert.Equal(string.Empty, only.MenuSectionDescription);
    }

    /// <summary>
    /// The walk preserves the order it is handed and re-decides nothing, which is the property a
    /// <c>GroupBy</c> would silently take away.
    ///
    /// <para>Headings are supplied in an order no alphabet and no identifier comparison would produce —
    /// Puddings before Drinks, with the identifiers ascending the other way — and both come back where they
    /// were put. §7's six-key ordering belongs to <c>IMenuDirectory</c> and is asserted against a real
    /// PostgreSQL; what belongs here is that this file does not have a second opinion about it.</para>
    /// </summary>
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

    /// <summary>
    /// Contiguity is a precondition rather than something this walk repairs, and the honest way to record
    /// that is to assert what a non-contiguous list actually produces: one group per <em>run</em>, not one
    /// per heading.
    ///
    /// <para>§7's ordering makes this arrangement unreachable through <c>IMenuDirectory</c>, so this is not
    /// a defect being tolerated — it is the walk's contract written down where a future caller can find it.
    /// The alternative implementations are both worse: a <c>GroupBy</c> re-decides the ordering in a second
    /// file, and throwing would put a run-time refusal on a guest's menu for a condition the query cannot
    /// produce. <b>Two groups sharing an identifier is the visible consequence</b>, and it is worth naming
    /// because a duplicated <c>@key</c> is how it would present on a surface.</para>
    /// </summary>
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

    /// <summary>
    /// One menu row. Positional, so that widening <see cref="MenuItemSummary"/> again breaks this helper
    /// once with CS7036 naming the parameter rather than compiling into eleven facts that mean something
    /// slightly different — which is F-84's mechanism and the reason that finding exists.
    /// </summary>
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
