using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

/// <summary>
/// The handheld layout contract (TECHNICAL_SPECIFICATION §11.12, §16.4).
///
/// <para><b>Why this is a test and not a review note.</b> F-59 is the whole argument. Four administration
/// index pages each declared their own eighty-line table vocabulary inline, each ending in
/// <c>.admin-row-actions { text-align: right; white-space: nowrap }</c> inside a wrapper with
/// <c>overflow-x: auto</c> — so on the 375px handset this software is used from, the only affordance on
/// the row sat off the right-hand edge of the screen. Nobody had decided that. It was four copies of one
/// paste, and no gate, test or document in this tree had an opinion about layout at any width, so the
/// only thing that could ever have found it was somebody holding a phone. Somebody did.</para>
///
/// <para><b>What it asserts, and what it deliberately cannot.</b> Whether a screen is comfortable is a
/// judgement and no test will make it. What is decidable is the four structural properties §11.12 states:
/// the stylesheet is written handheld-first through exactly one breakpoint; the shared vocabulary is
/// declared in one place; every cell in a record list carries the label that replaces the column header
/// it loses; and the per-page vocabularies this contract replaces are gone from everywhere they are not
/// still expected. Each of those is arithmetic on text, which is the level a gate can reach without
/// reporting findings on correct trees (F-41).</para>
///
/// <para><b>Why the forbidden list is small and lives beside its reason.</b> F-46's lesson: a rule stated
/// as a rule and enforced as a list of examples is enforced as a list of examples. So the list here is
/// two prefixes rather than an enumeration of class names, and the two names it pointedly does *not*
/// cover — <c>.chip</c> and <c>.visually-hidden</c>, both still declared inline by pages this slice did
/// not restructure — are named in <see cref="StillExpectedToCarryRetiredTableVocabulary"/>'s comment
/// with the slice that empties them. Extending the list is part of finishing the migration, not a chore
/// for afterwards.</para>
/// </summary>
public sealed class HandheldLayoutContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string StylesheetRelativePath = "src/MyRestaurant.WebApplication/wwwroot/app.css";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>
    /// The prefixes app.css owns. A component that declares a selector containing one of these is a page
    /// re-inventing the shared shape, which is the state F-59 was found in.
    /// </summary>
    private static readonly string[] SharedSelectorPrefixes = [".record-", ".page-head"];

    /// <summary>
    /// The per-page table vocabularies §11.12's record list replaces. Retired from four pages in M6 Slice
    /// 30; the remaining holders are listed in
    /// <see cref="StillExpectedToCarryRetiredTableVocabulary"/> below.
    /// </summary>
    private static readonly string[] RetiredWrapperClasses =
        ["admin-people", "admin-tables", "admin-menu", "admin-sittings", "admin-row-actions", "admin-header"];

    /// <summary>
    /// The files that may still use a retired name, and the reason the list is a list rather than an
    /// emptiness assertion: Slice 30 restructured the four administration <em>index</em> pages, which is
    /// where F-59 was reported, and left the four detail and explorer surfaces for Stage 1b of
    /// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>. Those four are ~2,400 lines of Razor whose tables are not
    /// record lists — a filter form, a per-sitting record, a device roster — and restructuring them
    /// blind, in the same slice, with no compiler on the authoring machine, is how a slice ships a build
    /// break.
    ///
    /// <para>This is F-47's shape, applied on purpose. The test keeps exactly one list — this one — and
    /// compares it against the set the tree actually produces, so the two can only agree by both being
    /// right. Finishing the migration means deleting entries from here, which is a decision somebody
    /// makes rather than an omission nobody notices; and a *new* page reaching for the old vocabulary
    /// fails immediately rather than joining a silent majority.</para>
    /// </summary>
    private static readonly string[] StillExpectedToCarryRetiredTableVocabulary =
    [
        "EventExplorer.razor",
        "HiddenRecords.razor",
        "ManageSitting.razor",
        "TableDisplays.razor",
    ];

    /// <summary>A Razor server-side comment, stripped before anything else is read.</summary>
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    /// <summary>A component's inline stylesheet.</summary>
    private static readonly Regex StyleBlock = new(@"<style>(.*?)</style>", RegexOptions.Singleline);

    /// <summary>
    /// A media query's condition — everything between <c>@media</c> and the opening brace of its block.
    /// </summary>
    private static readonly Regex MediaQuery = new(@"@media([^{]*)\{");

    /// <summary>
    /// §11.12: the layout is written for the narrow screen and widened by exactly one query.
    ///
    /// <para>All three halves of that sentence are asserted, because each fails differently. <b>Exactly
    /// one</b>, because a second breakpoint is the same number written in a second place, and the pair
    /// then drift — which is the mechanism behind F-48, F-50 and F-56 in three unrelated files.
    /// <b>min-width</b>, because the direction *is* the rule: a max-width query means the wide layout is
    /// the default and the handset is the exception, which is the arrangement that produced F-59.
    /// <b>No max-width anywhere</b>, because one page's exception is how a direction stops being one.
    /// </para>
    ///
    /// <para><c>prefers-reduced-motion</c> is a media query and is deliberately not counted: it says
    /// nothing about width, and a rule that reached it would be a gate with an opinion about
    /// accessibility preferences it cannot justify.</para>
    /// </summary>
    [Fact]
    public void TheStylesheetIsWrittenHandheldFirstThroughExactlyOneBreakpoint()
    {
        string stylesheet = ReadStylesheet();

        List<string> widthConditions = [];
        foreach (Match match in MediaQuery.Matches(stylesheet))
        {
            string condition = match.Groups[1].Value.Trim();
            if (condition.Contains("width", StringComparison.Ordinal))
            {
                widthConditions.Add(condition);
            }
        }

        Assert.True(
            widthConditions.Count == 1,
            $"{StylesheetRelativePath} must declare exactly one layout breakpoint (§11.12) and declares"
                + $" {widthConditions.Count}: {FormatList(widthConditions)}. A second breakpoint is the"
                + " same number written in a second place, and the two will disagree — that is the"
                + " mechanism of F-48, F-50 and F-56.");

        string only = widthConditions[0];

        Assert.True(
            only.Contains("min-width", StringComparison.Ordinal),
            $"{StylesheetRelativePath}'s one breakpoint is '{only}'. §11.12 requires it to be a"
                + " min-width: the handheld layout is what the file states unconditionally, and the"
                + " query is what widens it. A max-width query says the opposite — that the wide layout"
                + " is the default and the phone is the exception — which is the arrangement F-59 was"
                + " found in.");

        string[] maxWidthQueries = MediaQuery.Matches(stylesheet)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(condition => condition.Contains("max-width", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            maxWidthQueries.Length == 0,
            $"{StylesheetRelativePath} contains {maxWidthQueries.Length} max-width media"
                + $" query/queries: {FormatList(maxWidthQueries)}. One page's exception is how a"
                + " direction stops being a direction.");

        // Non-vacuity: an empty query would satisfy everything above and mean nothing. The real one
        // restores a table layout, a button row and a header row, which is well over ten declarations.
        int blockStart = stylesheet.IndexOf(only, StringComparison.Ordinal);
        string tail = stylesheet[blockStart..];
        Assert.True(
            tail.Count(character => character == ';') >= 10,
            $"{StylesheetRelativePath}'s breakpoint block looks empty. A query that widens nothing is a"
                + " rule satisfied by deleting the wide layout.");
    }

    /// <summary>
    /// The shared vocabulary is declared once. A component may still keep a rule nobody else reads —
    /// that is this project's standing arrangement for a statically linked stylesheet — but not one of
    /// these, because a second declaration of the same name is a page quietly overriding the contract
    /// (same specificity, later in the document, so the page always wins and app.css always loses).
    /// </summary>
    [Fact]
    public void NoComponentRedeclaresTheSharedHandheldVocabulary()
    {
        string stylesheet = ReadStylesheet();
        int blocksScanned = 0;
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                blocksScanned++;

                foreach (string prefix in SharedSelectorPrefixes)
                {
                    if (block.Groups[1].Value.Contains(prefix, StringComparison.Ordinal))
                    {
                        problems.Add($"{Path.GetFileName(path)} declares '{prefix}…' inline");
                    }
                }
            }
        }

        // Two non-vacuity guards, and they fail in opposite directions. The first catches a scan that
        // found no components at all; the second catches a shared vocabulary that does not exist, which
        // would make "nobody re-declares it" true and worthless.
        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so this scan is not looking at"
                + " the tree it is about. Razor comments are stripped first — a <style> mentioned inside"
                + " an @* … *@ comment is prose, and counting it would make this guard pass on nothing.");

        // Declared, not merely mentioned: the prefix has to begin a line, so a stylesheet that talks
        // about `.record-list` in a comment and defines nothing does not satisfy this.
        string[] stylesheetLines = stylesheet.Split('\n');
        string[] undeclared = SharedSelectorPrefixes
            .Where(prefix => !stylesheetLines.Any(
                line => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            $"{StylesheetRelativePath} declares no selector beginning {FormatList(undeclared)}, so this"
                + " test would pass on a tree with no shared vocabulary at all.");

        Assert.True(
            problems.Count == 0,
            "The §11.12 vocabulary is declared in app.css and nowhere else. Re-declared by:"
                + $" {FormatList(problems)}.");
    }

    /// <summary>
    /// Every cell in a record list says what it is.
    ///
    /// <para>This is the assertion that carries the most weight per character, and the reason is a
    /// property of browsers rather than of this project: overriding <c>display</c> on a table's parts
    /// drops the element's table semantics in every engine, so below the breakpoint the <c>&lt;thead&gt;</c>
    /// stops being what associates a cell with a column. A card whose cells have no <c>data-label</c> is
    /// therefore a column of bare values — <c>Table 4</c>, <c>2</c>, <c>19:04</c>, <c>£18.50</c> — with
    /// nothing on screen or in the accessibility tree saying which is which. The label is not decoration;
    /// it is the replacement for the header.</para>
    ///
    /// <para>Counting is exact rather than approximate because a record list page contains no other
    /// table: every <c>&lt;td&gt;</c> in these files is a record cell, and a cell whose content already
    /// says what it is opts out with <c>data-label=""</c> — which is a decision written down rather than
    /// an omission, and still counts here.</para>
    /// </summary>
    [Fact]
    public void EveryRecordListCellCarriesTheLabelThatReplacesItsColumnHeader()
    {
        int pagesChecked = 0;
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);
            if (!markup.Contains("class=\"record-list\"", StringComparison.Ordinal))
            {
                continue;
            }

            pagesChecked++;
            int cells = CountOccurrences(markup, "<td");
            int labels = CountOccurrences(markup, "data-label=");

            if (cells != labels)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)} has {cells} cells and {labels} labels"));
            }
        }

        Assert.True(
            pagesChecked >= 4,
            $"Only {pagesChecked} pages render a record list, and Slice 30 restructured four. Either the"
                + " wrapper class was renamed without this test, or a page lost its list.");

        Assert.True(
            problems.Count == 0,
            "A record-list cell without a data-label is a card cell with nothing saying what it holds,"
                + $" because overriding a table's display drops its header association: {FormatList(problems)}.");
    }

    /// <summary>
    /// The vocabulary §11.12 replaces has left every page that is not still expected to hold it. See
    /// <see cref="StillExpectedToCarryRetiredTableVocabulary"/> for why that set is not empty yet and
    /// what empties it.
    /// </summary>
    [Fact]
    public void StillExpectedToCarryRetiredTableVocabularyIsExactlyWhatTheTreeCarries()
    {
        SortedSet<string> found = [];

        foreach (string path in EnumerateComponents())
        {
            // Comments are stripped, so a page explaining that it is *namespaced like* the retired
            // vocabulary is prose and not a use of it. Three pages say exactly that.
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            if (RetiredWrapperClasses.Any(name => markup.Contains(name, StringComparison.Ordinal)))
            {
                found.Add(Path.GetFileName(path));
            }
        }

        SortedSet<string> expected = new(StillExpectedToCarryRetiredTableVocabulary, StringComparer.Ordinal);

        Assert.True(
            found.SetEquals(expected),
            $"The pages still carrying the retired per-page table vocabulary are {FormatList(found)};"
                + $" this test expects {FormatList(expected)}. If a page was converted, delete it from"
                + " StillExpectedToCarryRetiredTableVocabulary in the same commit — the list exists so"
                + " that finishing the migration is a decision rather than something nobody notices"
                + " (F-47). If a page acquired the old vocabulary, it is a new page reaching for the"
                + " shape F-59 was about.");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }

    private static string ReadStylesheet() => File.ReadAllText(PathTo(StylesheetRelativePath));

    private static IEnumerable<string> EnumerateComponents()
        => Directory
            .EnumerateFiles(PathTo(ComponentsRelativePath), "*.razor", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string PathTo(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                    + " §2 describes.");
        }

        return path;
    }

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other documentation and deployment contract
    /// tests use, and it fails rather than skips for the same reason: a check that quietly declines to
    /// run is worse than none.
    /// </summary>
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
