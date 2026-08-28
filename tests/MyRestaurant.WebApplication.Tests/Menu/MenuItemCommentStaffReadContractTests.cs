using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

public sealed class MenuItemCommentStaffReadContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private const string StaffPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor";

    private const string AdministrationPrefix = "Pages/Administration/";

    private const string WholeMenuRead = "ListAsync(";

    private const string PerPersonRead = "ListForPersonAsync(";

    private const string RowMarker = "data-comment-item=";

    private const string BodyMarker = "data-comment-body=";

    private const string CountMarker = "data-comment-count=";

    private const string LikeCountMarker = "data-like-count=";

    private const string PrimaryCell = "class=\"record-primary\"";

    private const string CountHelper = "private int? CommentCount(";

    private const string ClockCall = "RestaurantClock.DateAndTime(comment.OccurredAt)";

    private const string AuthorField = "comment.AuthorName";

    private const int MinimumComponentFiles = 40;

    private static readonly Regex InjectedCommentDirectory =
        new(@"@inject\s+IMenuItemCommentDirectory\s+(\w+)");

    private static readonly string[] SortVerbs =
    [
        "OrderBy(", "OrderByDescending(", "ThenBy(", ".Sort(", ".Reverse(",
    ];

    [Fact]
    public void TheStaffSurface_ReadsTheWholeMenu_AndNeverThePerPersonRead()
    {
        string page = StaffPage();

        Match injected = InjectedCommentDirectory.Match(page);

        Assert.True(
            injected.Success,
            $"§11.4's menu index does not inject IMenuItemCommentDirectory, so Stage 6e is unbuilt and"
            + " the two markers below have no receiver to qualify. Until this read has a caller it is a"
            + " read nobody makes, which §7 calls the same defect as a workflow verb nobody calls.");

        string receiver = injected.Groups[1].Value;

        Assert.True(
            page.Contains($"{receiver}.{WholeMenuRead}", StringComparison.Ordinal),
            $"§11.4's menu index never calls {receiver}.{WholeMenuRead}. §7 rules a comment"
            + " staff-facing, and the whole-menu read is the only read that answers for everybody —"
            + " this surface is where that ruling becomes something a person can act on.");

        Assert.False(
            page.Contains($"{receiver}.{PerPersonRead}", StringComparison.Ordinal),
            $"§11.4's menu index calls {receiver}.{PerPersonRead}, which answers with one person's"
            + " comments. The administrator is not the author of anything here; narrowing the staff"
            + " read to the signed-in person would render an empty page to the only people §7 lets"
            + " read these at all, and it would do it without failing.");
    }

    [Fact]
    public void TheWholeMenuRead_IsReachedFromNoSurfaceOutsideAdministration()
    {
        Component[] components = Components();

        Assert.True(
            components.Length >= MinimumComponentFiles,
            $"Only {components.Length} component(s) were read from {ComponentsRelativePath}/ and it"
            + $" holds at least {MinimumComponentFiles}. The finding below is an absence, and an"
            + " absence over a walk that opened nothing is the vacuous pass F-41 prohibits.");

        Component[] injectors = components
            .Where(component => InjectedCommentDirectory.IsMatch(component.Source))
            .ToArray();

        Assert.True(
            injectors.Length >= 2,
            $"Only {injectors.Length} component(s) inject IMenuItemCommentDirectory, and both halves of"
            + " this rule need a subject: §11.1's guest surface reads its own standing comment and"
            + " §11.4's index reads everybody's. With one injector the prohibition below is asserting"
            + " nothing, because the set it walks is the set it permits.");

        string[] readers = injectors
            .Where(component => component.Source.Contains(
                $"{InjectedCommentDirectory.Match(component.Source).Groups[1].Value}.{WholeMenuRead}",
                StringComparison.Ordinal))
            .Select(component => component.Path)
            .ToArray();

        Assert.True(
            readers.Length >= 1,
            "No component calls the whole-menu comment read, so Stage 6e is unbuilt and the"
            + " prohibition below is asserting nothing.");

        string[] outside = readers
            .Where(path => !path.StartsWith(AdministrationPrefix, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            outside.Length == 0,
            $"The whole-menu comment read is reached from outside /administration:"
            + $" {string.Join(", ", outside)}. §7 rules a comment staff-facing, and that ruling is what"
            + " makes the moderation question not arise — nothing a guest writes is rendered to another"
            + " guest, so there is nobody to moderate on behalf of. A surface outside this area that"
            + " reads everybody's comments reopens the plan's fourth Stage 6 row, and the row is what"
            + " has to be settled before the markup is written.");
    }

    [Fact]
    public void TheStaffRead_DeclaresItsDishAndItsSentence_AndTheChipIsAbsentWhereNobodyHasSpoken()
    {
        string page = StaffPage();

        Assert.True(
            page.Contains(RowMarker, StringComparison.Ordinal)
                && page.Contains(BodyMarker, StringComparison.Ordinal),
            $"§11.4's comment rows do not declare {RowMarker} and {BodyMarker}. A barrier that finds a"
            + " sentence by the column heading above it is asserting the copywriting, which is F-113's"
            + " ruling one register up and §11.1's reason for declaring the comment outcome beside it.");

        Assert.True(
            page.Contains(CountHelper, StringComparison.Ordinal),
            $"§11.4's menu index does not declare {CountHelper}. The chip is absent where nobody has"
            + " said anything rather than rendered as a nought, which is the like count's ruling — a"
            + " column of noughts is a verdict on sixty dishes nobody has been asked about — and an"
            + " absence is only representable where the count is nullable.");

        int chips = Occurrences(page, CountMarker);

        Assert.True(
            chips == 1,
            $"§11.4 renders one comment chip and the markup holds {chips}. Zero is the chip unbuilt,"
            + " and two is a second copy in a second place, which is the shape that drifts.");

        int primary = page.IndexOf(PrimaryCell, StringComparison.Ordinal);

        Assert.True(primary >= 0, $"§11.4's item rows no longer carry a {PrimaryCell} cell.");

        int cellEnd = page.IndexOf("</td>", primary, StringComparison.Ordinal);
        int chip = page.IndexOf(CountMarker, StringComparison.Ordinal);
        int like = page.IndexOf(LikeCountMarker, StringComparison.Ordinal);

        Assert.True(
            chip > primary && chip < cellEnd && like > primary && like < cellEnd,
            "§11.4's comment chip is not in the same cell as the dish's name and the like count. Both"
            + " are beside the name rather than in columns of their own, because a column exists on"
            + " every row and a chip exists where there is something to say — and below the breakpoint"
            + " a column is a labelled line on every card whether or not anybody has spoken.");
    }

    [Fact]
    public void TheStaffRead_NamesWhoSaidItAndWhen_ThroughTheOneClock()
    {
        string page = StaffPage();

        Assert.True(
            page.Contains(AuthorField, StringComparison.Ordinal),
            $"§11.4's comment rows do not render {AuthorField}. A sentence with no author is a sentence"
            + " staff cannot act on, and the read already carries the name — `AuthorName` is the"
            + " display name where there is one and the username otherwise, resolved in the query"
            + " rather than on the surface.");

        Assert.True(
            page.Contains(ClockCall, StringComparison.Ordinal),
            $"§11.4's comment rows do not render the instant through {ClockCall}. §11.7 renders every"
            + " instant in `RESTAURANT_TIME_ZONE` through one type for every reader, and a row that"
            + " formats its own is a second convention on a page that already states one at its foot"
            + " (F-36).");

        Assert.DoesNotContain("ToString(\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStaffRead_IsInTheMenusOwnOrder_AndThisSurfaceSortsNothing()
    {
        string page = StaffPage();

        foreach (string verb in SortVerbs)
        {
            Assert.False(
                page.Contains(verb, StringComparison.Ordinal),
                $"§11.4's menu index writes {verb}. §7 makes every menu read's order total and ends it"
                + " in the identifier precisely so that a tie is broken the same way on two reads; a"
                + " surface that sorts is a second authority on order, and the two disagree the first"
                + " time two comments share an instant. The dishes arrive in §7's order and the"
                + " comments arrive newest-first within a dish, so this page projects one through the"
                + " other and orders nothing itself.");
        }

        Assert.True(
            page.Contains("SelectMany(", StringComparison.Ordinal),
            "§11.4's comment block is not projected through the item list, so its order is the read's"
            + " own — which sorts on `menu_item_identifier` and is therefore a UUID ordering presented"
            + " to a person as though it were the menu's. Grouping by dish in the order the menu"
            + " already declares is what makes the block readable without sorting anything.");
    }

    private static string StaffPage() => File.ReadAllText(PathUnder(StaffPageRelativePath));

    private sealed record Component(string Path, string Source);

    private static Component[] Components()
    {
        string root = PathUnder(ComponentsRelativePath);

        return Directory
            .EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new Component(
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(path)))
            .ToArray();
    }

    private static int Occurrences(string text, string value)
    {
        int found = 0;

        for (int at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static string PathUnder(string relativePath)
        => Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }
}
