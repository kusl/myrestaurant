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
/// judgement and no test will make it. What is decidable is the structural properties §11.12 states:
/// the <em>tree</em> is written handheld-first through exactly one breakpoint; the shared vocabulary is
/// declared in one place; every cell in a record list carries the label that replaces the column header
/// it loses; the per-page vocabularies this contract replaces are gone from everywhere they are not
/// still expected; the row of area links is rendered once rather than pasted per page; and every custom
/// property the tree reads is one the tree declares. Each is arithmetic on text, which is the level a
/// gate can reach without reporting findings on correct trees (F-41).</para>
///
/// <para><b>Two of the six facts are here because the other four were not enough</b>, and both gaps had
/// the same shape. The breakpoint fact read <c>app.css</c> and nothing else, so "exactly one breakpoint"
/// — a rule about the tree — was enforced about one file, and a component could have declared a second
/// one in an inline <c>&lt;style&gt;</c> with nothing to notice (<b>F-63</b>). And nothing anywhere
/// asserted that a <c>var(--name)</c> a component reads is a name <c>:root</c> declares, so five
/// properties were read fifty-five times across eight components and declared nowhere, every reference
/// silently falling through to a hard-coded literal (<b>F-64</b>). Both are F-46's lesson: a rule stated
/// as a rule and enforced against the file that prompted it is enforced against the file that prompted
/// it.</para>
///
/// <para><b>Why the forbidden list is small and lives beside its reason.</b> Same lesson, applied to the
/// shape of this file. The list is three prefixes rather than an enumeration of class names, and the two
/// names it pointedly does <em>not</em> cover — <c>.chip</c> and <c>.visually-hidden</c>, both still
/// declared inline by pages Stage 1b has not reached — are named in
/// <see cref="StillExpectedToCarryRetiredTableVocabulary"/>'s comment with the slice that empties them.
/// Extending the list is part of finishing the migration, not a chore for afterwards.</para>
/// </summary>
public sealed class HandheldLayoutContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string StylesheetRelativePath = "src/MyRestaurant.WebApplication/wwwroot/app.css";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>
    /// The prefixes app.css owns. A component that declares a selector containing one of these is a page
    /// re-inventing the shared shape, which is the state F-59 was found in. <c>.filter-</c> joined them
    /// in M6 Slice 33, when the two administration explorers stopped carrying an inline copy each of the
    /// same twelve-line filter form.
    /// </summary>
    private static readonly string[] SharedSelectorPrefixes = [".record-", ".page-head", ".filter-"];

    /// <summary>
    /// The per-page table vocabularies §11.12's record list replaces. Retired from four pages in M6 Slice
    /// 30 and from two more in Slice 33; the remaining holders are listed in
    /// <see cref="StillExpectedToCarryRetiredTableVocabulary"/> below.
    /// </summary>
    private static readonly string[] RetiredWrapperClasses =
        ["admin-people", "admin-tables", "admin-menu", "admin-sittings", "admin-row-actions", "admin-header"];

    /// <summary>
    /// The files that may still use a retired name, and the reason the list is a list rather than an
    /// emptiness assertion: the migration is staged across slices, and each stage is a decision somebody
    /// takes rather than a page nobody remembers. Slice 30 restructured the four administration
    /// <em>index</em> pages, which is where F-59 was reported. Slice 33 converted the two explorers,
    /// <c>EventExplorer</c> and <c>HiddenRecords</c> — the last two carrying a hand-rolled row of area
    /// links, which is why those two went together. What is left is the two detail surfaces, whose tables
    /// are not record lists at all: a device roster with a pair-code panel, and one sitting's complete
    /// record.
    ///
    /// <para>This is F-47's shape, applied on purpose. The test keeps exactly one list — this one — and
    /// compares it against the set the tree actually produces, so the two can only agree by both being
    /// right. Finishing the migration means deleting entries from here; and a <em>new</em> page reaching
    /// for the old vocabulary fails immediately rather than joining a silent majority.</para>
    /// </summary>
    private static readonly string[] StillExpectedToCarryRetiredTableVocabulary =
    [
        "ManageSitting.razor",
        "TableDisplays.razor",
    ];

    /// <summary>
    /// §11.4's six administration surfaces, as paths. The subject of
    /// <see cref="TheAdministrationAreaRowIsRenderedOnceRatherThanPastedPerPage"/>, and the same six
    /// <c>AdministrationAreaLinks</c> renders — restated here rather than read out of that component,
    /// because a test that took its expectations from the file under test would pass on a file that had
    /// lost half its links.
    /// </summary>
    private static readonly string[] AdministrationAreaPaths =
    [
        "/administration",
        "/administration/tables",
        "/administration/menu",
        "/administration/sittings",
        "/administration/hidden-records",
        "/administration/events",
    ];

    /// <summary>
    /// The one component allowed to name every area path, because rendering them once is its entire job.
    /// Exempt by literal filename, the way <c>export.sh</c> is exempt from the separator gate — an
    /// exemption a reader can see is an exemption somebody decided on.
    /// </summary>
    private const string AreaLinksComponentFileName = "AdministrationAreaLinks.razor";

    /// <summary>A Razor server-side comment, stripped before anything else is read.</summary>
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    /// <summary>A CSS comment, stripped before a stylesheet's declarations are read.</summary>
    private static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    /// <summary>A component's inline stylesheet.</summary>
    private static readonly Regex StyleBlock = new(@"<style>(.*?)</style>", RegexOptions.Singleline);

    /// <summary>
    /// A media query's condition — everything between <c>@media</c> and the opening brace of its block.
    /// </summary>
    private static readonly Regex MediaQuery = new(@"@media([^{]*)\{");

    /// <summary>A custom property being declared: <c>--name:</c> at the head of a declaration.</summary>
    private static readonly Regex CustomPropertyDeclaration = new(@"(?m)^\s*(--[a-z0-9-]+)\s*:");

    /// <summary>A custom property being read: the name inside a <c>var()</c>, fallback or not.</summary>
    private static readonly Regex CustomPropertyReference = new(@"var\(\s*(--[a-z0-9-]+)");

    /// <summary>
    /// An ordinary link's target, as written in the markup. Only literal hrefs are read: a computed one
    /// (<c>href="@SomePath(entry)"</c>) is not a hand-pasted area link and could not be compared against
    /// a path without evaluating it.
    /// </summary>
    private static readonly Regex LiteralHref = new(@"href=""(/[^""@]*)""");

    /// <summary>The route a routable component declares, so a page's link to itself can be discounted.</summary>
    private static readonly Regex PageDirective = new(@"(?m)^@page\s+""([^""]+)""");

    /// <summary>
    /// §11.12: the layout is written for the narrow screen and widened by exactly one query — and that is
    /// a rule about the <b>tree</b>, which is the correction this fact carries (<b>F-63</b>).
    ///
    /// <para>The previous version read <c>app.css</c> and stopped. Every component in this application
    /// may keep an inline <c>&lt;style&gt;</c> for rules nobody else reads, because <c>App.razor</c>
    /// links the static stylesheet rather than the scoped bundle — so a width query inside one of those
    /// blocks is a second breakpoint in every sense that matters, and was a second breakpoint no
    /// assertion in this tree could see. No component had one, so the rule was true; it was simply not
    /// enforced, which is the state F-59 was found in one level down.</para>
    ///
    /// <para>All four halves are asserted, because each fails differently. <b>Exactly one</b>, because a
    /// second is the same number written in a second place, and the pair then drift — the mechanism
    /// behind F-48, F-50 and F-56 in three unrelated files. <b>In app.css</b>, because "one breakpoint,
    /// in a component" satisfies a count and abandons the arrangement. <b>min-width</b>, because the
    /// direction <em>is</em> the rule: a max-width query means the wide layout is the default and the
    /// handset is the exception, which is the arrangement that produced F-59. <b>No max-width
    /// anywhere</b>, because one page's exception is how a direction stops being one.</para>
    ///
    /// <para><c>prefers-reduced-motion</c> is a media query and is deliberately not counted as a
    /// breakpoint: it says nothing about width, and a rule that reached it would be a gate with an
    /// opinion about accessibility preferences it cannot justify. A component may not declare one
    /// either, though, and that is the second assertion below rather than an oversight — a motion
    /// preference is exactly as much a property of the reader rather than of one page.</para>
    /// </summary>
    [Fact]
    public void TheTreeIsWrittenHandheldFirstThroughExactlyOneBreakpoint()
    {
        string stylesheet = ReadStylesheet();

        List<string> widthConditions = [];
        List<string> componentQueries = [];
        int blocksScanned = 0;

        foreach (Match match in MediaQuery.Matches(stylesheet))
        {
            string condition = match.Groups[1].Value.Trim();
            if (condition.Contains("width", StringComparison.Ordinal))
            {
                widthConditions.Add($"{StylesheetRelativePath}: {condition}");
            }
        }

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                blocksScanned++;

                foreach (Match match in MediaQuery.Matches(block.Groups[1].Value))
                {
                    string condition = match.Groups[1].Value.Trim();
                    string reported = $"{Path.GetFileName(path)}: {condition}";

                    componentQueries.Add(reported);

                    if (condition.Contains("width", StringComparison.Ordinal))
                    {
                        widthConditions.Add(reported);
                    }
                }
            }
        }

        // Non-vacuity, and it is the guard the F-63 half needs: a scan that found no component
        // stylesheets would report "no component declares a query" while having looked at nothing.
        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so the F-63 half of this fact is"
                + " not looking at the tree it is about. Razor comments are stripped first — a <style>"
                + " mentioned inside an @* … *@ comment is prose.");

        // Its own assertion rather than folded into the count, because the message a reader needs is
        // different: the count says "there are two of these", this says "yours is in the wrong file".
        Assert.True(
            componentQueries.Count == 0,
            $"§11.12's one breakpoint lives in {StylesheetRelativePath} and nowhere else. Declared"
                + $" inline by: {FormatList(componentQueries)}. A component may keep rules nobody else"
                + " reads; a media query is not one of those, because it is the arrangement rather than a"
                + " detail of one page. Put the wide-layout difference in the single query at the bottom"
                + " of app.css, beside every other page's (F-63).");

        Assert.True(
            widthConditions.Count == 1,
            $"The tree must declare exactly one layout breakpoint (§11.12) and declares"
                + $" {widthConditions.Count}: {FormatList(widthConditions)}. A second breakpoint is the"
                + " same number written in a second place, and the two will disagree — that is the"
                + " mechanism of F-48, F-50 and F-56.");

        string only = widthConditions[0];

        Assert.True(
            only.StartsWith(StylesheetRelativePath, StringComparison.Ordinal),
            $"The one breakpoint is declared in '{only}' rather than in {StylesheetRelativePath}. The"
                + " count above is satisfied and the arrangement is not: every page's wide layout is read"
                + " from one query, in one file.");

        Assert.True(
            only.Contains("min-width", StringComparison.Ordinal),
            $"{StylesheetRelativePath}'s one breakpoint is '{only}'. §11.12 requires it to be a"
                + " min-width: the handheld layout is what the file states unconditionally, and the query"
                + " is what widens it. A max-width query says the opposite — that the wide layout is the"
                + " default and the phone is the exception — which is the arrangement F-59 was found in.");

        string[] maxWidthQueries = MediaQuery.Matches(stylesheet)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(condition => condition.Contains("max-width", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            maxWidthQueries.Length == 0,
            $"{StylesheetRelativePath} contains {maxWidthQueries.Length} max-width media"
                + $" query/queries: {FormatList(maxWidthQueries)}. One page's exception is how a direction"
                + " stops being a direction.");

        // Non-vacuity: an empty query would satisfy everything above and mean nothing. The real one
        // restores a table layout, a button row and a header row, which is well over ten declarations.
        // The condition is recovered from the reported line by dropping the file prefix this fact added.
        string condition48 = only[(only.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        int blockStart = stylesheet.IndexOf(condition48, StringComparison.Ordinal);
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

    /// <summary>
    /// §11.4's row of area links is rendered once, by the component that exists to render it.
    ///
    /// <para><b>Why this needed a fact of its own.</b> F-59's resolution says the six links were
    /// copy-pasted into six pages and that each copy omitted a different one — its own — so the row was a
    /// different row on every screen and no page was reachable from every other. That sentence has been
    /// in the ledger, in §11.12 and in <c>AdministrationAreaLinks</c>'s own doc comment since Slice 30,
    /// and nothing in the tree enforced it: Slice 30 converted four pages, Slice 33 converted the last
    /// two, and a seventh administration page written tomorrow could paste the row back with every gate
    /// still green. This is F-47's habit — where the rule can be executed, a list of the pages that obey
    /// it must not stand in for it — applied to a rule that did not even have the list.</para>
    ///
    /// <para><b>How a hand-rolled row is told from a legitimate link.</b> By counting the distinct area
    /// paths a component names literally, <em>excluding its own route</em>. A hand-rolled row names five
    /// or six. A page with a "Back to tables" link names one. The exclusion is what makes the threshold
    /// two rather than a fudged three: <c>HiddenRecords</c> legitimately links to its own path — that is
    /// the "Show everything" filter reset — so without discounting the self-link a correct tree would
    /// report a finding (F-41). Only literal <c>href</c> values are read; a computed one is not a pasted
    /// link and could not be compared against a path without evaluating it.</para>
    /// </summary>
    [Fact]
    public void TheAdministrationAreaRowIsRenderedOnceRatherThanPastedPerPage()
    {
        int componentsScanned = 0;
        SortedSet<string> renderers = [];
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            string fileName = Path.GetFileName(path);
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            componentsScanned++;

            if (markup.Contains("<AdministrationAreaLinks", StringComparison.Ordinal))
            {
                renderers.Add(fileName);
            }

            if (string.Equals(fileName, AreaLinksComponentFileName, StringComparison.Ordinal))
            {
                continue;
            }

            Match route = PageDirective.Match(markup);
            string ownRoute = route.Success ? route.Groups[1].Value : string.Empty;

            SortedSet<string> named = [];
            foreach (Match href in LiteralHref.Matches(markup))
            {
                string target = href.Groups[1].Value;

                if (AdministrationAreaPaths.Contains(target, StringComparer.Ordinal)
                    && !string.Equals(target, ownRoute, StringComparison.Ordinal))
                {
                    named.Add(target);
                }
            }

            if (named.Count >= 2)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{fileName} links to {named.Count} area paths besides its own ({FormatList(named)})"));
            }
        }

        // Non-vacuity, in both directions. The scan has to have read the tree, and the shared component
        // has to still name every area — a row that had lost two links would satisfy every count below
        // while being exactly the defect this fact is about.
        Assert.True(
            componentsScanned >= 20,
            $"Only {componentsScanned} components were scanned, so this fact is not looking at the tree"
                + " it is about.");

        string areaLinksMarkup = RazorComment.Replace(
            File.ReadAllText(Path.Combine(
                PathTo(ComponentsRelativePath),
                "Pages",
                "Administration",
                AreaLinksComponentFileName)),
            string.Empty);

        string[] missing = AdministrationAreaPaths
            .Where(area => !areaLinksMarkup.Contains($"\"{area}\"", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{AreaLinksComponentFileName} does not name {FormatList(missing)}. It is the one place the"
                + " set is declared, so a link missing from it is a surface no page can reach — which is"
                + " the F-59 defect with the paste removed and the omission kept.");

        Assert.True(
            renderers.Count >= 6,
            $"Only {renderers.Count} component(s) render <AdministrationAreaLinks />, and §11.4 has six"
                + $" administration surfaces: {FormatList(renderers)}. A page that dropped the row does"
                + " not fail the assertion below, because a page with no links names no paths.");

        Assert.True(
            problems.Count == 0,
            "§11.4's area row is rendered by AdministrationAreaLinks and pasted nowhere:"
                + $" {FormatList(problems)}. A page that names two or more area paths besides its own"
                + " route has its own copy of the row, and every copy in F-59 omitted a different link —"
                + " so no page was reachable from every other. Render <AdministrationAreaLinks"
                + " Current=\"…\" /> inside the .page-head instead.");
    }

    /// <summary>
    /// Every custom property the tree reads is one the tree declares (<b>F-64</b>).
    ///
    /// <para><b>Why this is a finding and not tidiness.</b> <c>var(--muted-foreground, #666)</c> is valid
    /// CSS whether or not <c>--muted-foreground</c> exists, and no browser, linter or build step in this
    /// stack says a word either way: an undeclared name simply renders its fallback. Five names —
    /// <c>--muted-foreground</c>, <c>--rule</c>, <c>--surface-sunken</c>, <c>--chip-background</c> and
    /// <c>--chip-foreground</c> — were read fifty-five times across eight components and declared
    /// nowhere, so eight administration and counter surfaces rendered <c>#666</c> greys and
    /// <c>#e5e5e5</c> hairlines while every other surface rendered <c>--ink-soft</c> and
    /// <c>--hairline</c>. The palette in <c>:root</c> was not the palette on the screen, and the
    /// difference was invisible in review precisely because a fallback is what a careful author
    /// writes.</para>
    ///
    /// <para><b>What this deliberately does not assert.</b> That a reference to a <em>declared</em>
    /// property carries no fallback. Over a hundred references across sixteen components still do, and
    /// they are harmless where the name exists — dead code rather than a wrong colour. §11.12 states the
    /// rule and Stage 1b removes them as it empties each block, but a gate that failed on them today
    /// would be reporting a finding on a tree whose colours are all correct (F-41).</para>
    /// </summary>
    [Fact]
    public void EveryCustomPropertyTheTreeReadsIsDeclaredInTheStylesheet()
    {
        string stylesheet = CssComment.Replace(ReadStylesheet(), string.Empty);

        SortedSet<string> declared = [];
        foreach (Match match in CustomPropertyDeclaration.Matches(stylesheet))
        {
            declared.Add(match.Groups[1].Value);
        }

        SortedSet<string> referenced = [];
        List<string> problems = [];

        void Read(string source, string where)
        {
            foreach (Match match in CustomPropertyReference.Matches(source))
            {
                string name = match.Groups[1].Value;
                referenced.Add(name);

                if (!declared.Contains(name))
                {
                    problems.Add($"{where} reads {name}");
                }
            }
        }

        Read(stylesheet, Path.GetFileName(StylesheetRelativePath));

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                Read(CssComment.Replace(block.Groups[1].Value, string.Empty), Path.GetFileName(path));
            }
        }

        // Non-vacuity, both directions. A scan that read no references would report a clean tree having
        // looked at nothing, and a stylesheet whose :root had moved would make every reference a finding
        // — the first guard is what tells "nothing is declared" apart from "one name is missing".
        Assert.True(
            declared.Count >= 10,
            $"Only {declared.Count} custom properties are declared in {StylesheetRelativePath}, and :root"
                + " carries well over ten. Either the declaration pattern no longer matches the file or"
                + " the palette has moved, and in both cases every reference below would be reported as a"
                + " finding on a correct tree (F-41).");

        Assert.True(
            referenced.Count >= 10,
            $"Only {referenced.Count} distinct custom properties are read anywhere in the tree, so this"
                + " scan is not reading the stylesheets it is about.");

        // De-duplicated: one wrong name read thirty times is one finding stated thirty times, and what a
        // reader needs from the message is which names to fix.
        string[] distinct = problems
            .Distinct(StringComparer.Ordinal)
            .OrderBy(problem => problem, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            distinct.Length == 0,
            $"{distinct.Length} reference(s) name a custom property nothing declares:"
                + $" {FormatList(distinct)}. An undeclared property is not an error in CSS — it renders"
                + " the fallback beside it — so the rule keeps working and quietly stops using the"
                + " palette. Either declare the name in app.css's :root, or name the property that"
                + " already means what the rule wants (F-64).");
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
