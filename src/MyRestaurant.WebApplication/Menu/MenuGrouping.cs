using MyRestaurant.DataAccess.Menu;

namespace MyRestaurant.WebApplication.Menu;

public sealed record MenuHeadingGroup(
    Guid MenuSectionIdentifier,
    string MenuSectionName,
    string MenuSectionDescription,
    bool MenuSectionIsActive,
    IReadOnlyList<MenuItemSummary> Items);

public static class MenuGrouping
{
    public static IReadOnlyList<MenuHeadingGroup> VisibleToGuests(IReadOnlyList<MenuItemSummary> menuItems)
    {
        ArgumentNullException.ThrowIfNull(menuItems);

        return Walk(menuItems, includeHeadingsHiddenFromGuests: false);
    }

    public static IReadOnlyList<MenuHeadingGroup> EveryHeading(IReadOnlyList<MenuItemSummary> menuItems)
    {
        ArgumentNullException.ThrowIfNull(menuItems);

        return Walk(menuItems, includeHeadingsHiddenFromGuests: true);
    }

    private static IReadOnlyList<MenuHeadingGroup> Walk(
        IReadOnlyList<MenuItemSummary> menuItems,
        bool includeHeadingsHiddenFromGuests)
    {
        List<MenuHeadingGroup> headings = [];
        List<MenuItemSummary> current = [];

        Guid currentIdentifier = Guid.Empty;
        string currentName = string.Empty;
        string currentDescription = string.Empty;
        bool currentIsActive = false;

        foreach (MenuItemSummary item in menuItems)
        {
            if (!item.MenuSectionIsActive && !includeHeadingsHiddenFromGuests)
            {
                continue;
            }

            if (current.Count > 0 && item.MenuSectionIdentifier != currentIdentifier)
            {
                headings.Add(new MenuHeadingGroup(
                    currentIdentifier, currentName, currentDescription, currentIsActive, current));
                current = [];
            }

            if (current.Count == 0)
            {
                currentIdentifier = item.MenuSectionIdentifier;
                currentName = item.MenuSectionName;
                currentDescription = item.MenuSectionDescription;
                currentIsActive = item.MenuSectionIsActive;
            }

            current.Add(item);
        }

        if (current.Count > 0)
        {
            headings.Add(new MenuHeadingGroup(
                currentIdentifier, currentName, currentDescription, currentIsActive, current));
        }

        return headings;
    }
}
