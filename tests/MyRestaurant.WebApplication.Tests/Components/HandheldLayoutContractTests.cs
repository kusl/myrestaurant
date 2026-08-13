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
/// <para><b>Three of the seven facts are here because the other four were not enough</b>, and all three
/// gaps had the same shape. The breakpoint fact read <c>app.css</c> and nothing else, so "exactly one
/// breakpoint" — a rule about the tree — was enforced about one file, and a component could have declared
/// a second one in an inline <c>&lt;style&gt;</c> with nothing to notice (<b>F-63</b>). Nothing anywhere
/// asserted that a <c>var(--name)</c> a component reads is a name <c>:root</c> declares, so five
/// properties were read fifty-five times across eight components and declared nowhere, every reference
/// silently falling through to a hard-coded literal (<b>F-64</b>). And nothing asserted the one number
/// §11.12 states outright, so two control rules in <c>app.css</c> sat eight pixels under it, one of them
/// under a comment claiming otherwise (<b>F-65</b>). Each is F-46's lesson: a rule stated as a rule and
/// enforced against the file that prompted it is enforced against the file that prompted it.</para>
///
/// <para><b>Why the forbidden list is a list of prefixes.</b> Same lesson, applied to the shape of this
/// file: seven prefixes rather than an enumeration of class names, so a shared name nobody has invented
/// yet is covered. Extending it is what finishing a stage of the migration <em>means</em> — and until
/// Slice 34 it could not be extended past three, because the scan behind it read a
/// <c>&lt;style&gt;</c> block as text and three components describe the shared vocabulary in a CSS
/// comment (<b>F-67</b>). A gate whose reach is bounded by which names appear in somebody's prose is a
/// gate about prose.</para>
///
/// <para><b>Nine facts, and the eighth is the one worth reading the history of.</b> Seven landed across
/// Slices 30, 33 and 34. The eighth — <see cref="OverflowWrapIsDeclaredExactlyOnceOnTheBodyElement"/> —
/// arrived in the tree with no ledger row, no §16.4 paragraph, no changelog entry and no line in
/// <c>_CHANGES.md</c>, which is every artefact this project's atomic-documentation rule requires of a
/// behaviour change. It is a good rule and it is kept; what was missing was the paperwork, and the
/// arithmetic that would have shown it up was performed and then not read — Slice 34 predicted 1077
/// tests and the run returned 1078 (<b>F-70</b>). The ninth is the palette (<b>F-68</b>,
/// <b>F-69</b>).</para>
/// </summary>
public sealed class HandheldLayoutContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string StylesheetRelativePath = "src/MyRestaurant.WebApplication/wwwroot/app.css";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>
    /// The prefixes app.css owns. A component that declares a selector beginning with one of these is a
    /// page re-inventing the shared shape, which is the state F-59 was found in. <c>.filter-</c> joined
    /// them in M6 Slice 33, when the two administration explorers stopped carrying an inline copy each of
    /// the same twelve-line filter form; <c>.manage-</c>, <c>.chip</c>, <c>.muted</c> and
    /// <c>.visually-hidden</c> joined in Slice 34, when the four detail surfaces stopped carrying an
    /// inline copy each of the detail vocabulary and of the chip set (<b>F-66</b>).
    ///
    /// <para><b>Extending this list is what finishing the migration means</b>, and it could not be
    /// extended until the gate below could tell a declaration from a sentence about one (<b>F-67</b>).
    /// <c>KitchenBoard</c>, <c>CounterBoard</c> and <c>CounterSitting</c> each explain in a CSS comment
    /// which names they lean on from app.css, and three of those names are on this list — so under a scan
    /// that read a block as text, adding <c>.chip</c> here reported a finding on three correct
    /// pages.</para>
    /// </summary>
    private static readonly string[] SharedSelectorPrefixes =
        [".record-", ".page-head", ".filter-", ".manage-", ".chip", ".muted", ".visually-hidden"];

    /// <summary>
    /// The per-page table vocabularies §11.12's record list replaces. Retired from four pages in M6 Slice
    /// 30, from the two explorers in Slice 33, and from the last two — <c>TableDisplays</c> and
    /// <c>ManageSitting</c> — in Slice 34, which is why
    /// <see cref="TheRetiredTableVocabularyHasLeftTheTree"/> is now an emptiness assertion and no longer
    /// carries a list of who still holds one.
    /// </summary>
    private static readonly string[] RetiredWrapperClasses =
        ["admin-people", "admin-tables", "admin-menu", "admin-sittings", "admin-row-actions", "admin-header"];

    /// <summary>
    /// §11.12's touch-target minimum in CSS pixels: <c>--touch-target</c> is <c>2.75rem</c>, 44px at the
    /// default root size. Written as the number here for the same reason
    /// <c>HandheldReach.MinimumTouchTargetPixels</c> is — the rule is about the height a finger has to
    /// hit, and a check that read the value back out of the stylesheet would be satisfied by a stylesheet
    /// that had lowered it.
    /// </summary>
    private const double MinimumTouchTargetPixels = 44.0;

    /// <summary>The default root font size, which is what turns a <c>rem</c> in this tree into pixels.</summary>
    private const double PixelsPerRem = 16.0;

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

    /// <summary>
    /// A <c>min-height</c> and whatever it was given, up to the end of the declaration.
    /// </summary>
    private static readonly Regex MinimumHeightDeclaration = new(@"min-height\s*:\s*([^;}]+)");

    /// <summary>A length in <c>rem</c> or <c>px</c>, which are the two units this tree writes heights in.</summary>
    private static readonly Regex CssLength = new(@"^([0-9]*\.?[0-9]+)(rem|px)$");

    /// <summary>Anything a combinator or a descendant space can separate two simple selectors with.</summary>
    private static readonly char[] SelectorSeparators = [' ', '\t', '\n', '\r', '>', '+', '~'];

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
    /// A colour written as a value: a hex triple, quad, sextet or octet, or an <c>rgb()</c> /
    /// <c>hsl()</c> function with or without alpha.
    ///
    /// <para><c>rgb()</c> is here on purpose. <c>rgba(22, 32, 43, 0.04)</c> is <c>--ink</c> written in
    /// decimal, and those three were the only duplicates of the palette in <c>app.css</c> that a scan
    /// for <c>#hex</c> could never have found (<b>F-68</b>).</para>
    ///
    /// <para><c>transparent</c> and <c>currentColor</c> are deliberately not matched. They are keywords
    /// rather than values: neither names a colour that could drift from another copy of itself, which is
    /// the whole failure mode this pattern exists to find.</para>
    /// </summary>
    private static readonly Regex ColourLiteral =
        new(@"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|hsla?\([^)]*\)");

    /// <summary>
    /// The palette: <c>:root</c> and its declarations. Matched after CSS comments are stripped, which is
    /// what makes <c>[^{}]*</c> safe — a declaration list contains no nested braces, while two of the
    /// comments inside this particular block are prose that mentions them.
    /// </summary>
    private static readonly Regex PaletteBlock = new(@":root\s*\{[^{}]*\}");

    /// <summary>
    /// A <c>var()</c> reference carrying a fallback — the name, then a comma. What follows the comma is
    /// not captured, because the finding is the comma (<b>F-69</b>): a fallback on a declared property is
    /// dead code whatever it says, and a fallback is exactly what made an <em>undeclared</em> property
    /// indistinguishable from a declared one in review across eight components (F-64).
    /// </summary>
    private static readonly Regex CustomPropertyFallback = new(@"var\(\s*--[a-z0-9-]+\s*,");

    /// <summary>
    /// The <c>overflow-wrap</c> property and whatever it was given, up to the end of the declaration.
    /// </summary>
    private static readonly Regex OverflowWrapDeclaration = new(@"overflow-wrap\s*:\s*([^;}]+)");

    /// <summary>The <c>body</c> element's own rule, so a declaration on it can be told from one near it.</summary>
    private static readonly Regex BodyRule = new(@"(?m)^body\s*\{([^}]+)\}");

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
    ///
    /// <para><b>It reads declarations rather than text, and that is the correction it carries
    /// (F-67).</b> The previous version asked whether the prefix appeared <em>anywhere</em> in the
    /// <c>&lt;style&gt;</c> block. Razor comments were stripped first and CSS comments were not — while
    /// the sibling custom-property fact in this same file stripped both — so a page that explained in a
    /// comment which shared names it leans on was indistinguishable from a page that re-declared them.
    /// Three components do exactly that, naming <c>.chip</c> and <c>.muted</c> in prose, which meant the
    /// list above could only ever hold prefixes that happened not to appear in anybody's sentence. That
    /// is not a rule about the tree; it is a rule about the tree's comments, and it bounded the migration
    /// Stage 1b was written to finish.</para>
    ///
    /// <para>The standard applied is the one this fact already applied to <c>app.css</c> two assertions
    /// below — <em>declared, not merely mentioned</em> — and it now applies to both sides. A prefix
    /// matches when it begins a simple selector in a rule's prelude: <c>.chip-ok</c> matches
    /// <c>.chip</c>, and so does <c>.sitting-record .muted</c>, which is a page overriding a shared name
    /// at higher specificity and is the harder half of the same defect.</para>
    /// </summary>
    [Fact]
    public void NoComponentRedeclaresTheSharedHandheldVocabulary()
    {
        int blocksScanned = 0;
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                blocksScanned++;

                foreach (string selector in SimpleSelectorsDeclaredIn(block.Groups[1].Value))
                {
                    foreach (string prefix in SharedSelectorPrefixes)
                    {
                        if (selector.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            problems.Add($"{Path.GetFileName(path)} declares '{selector}' inline");
                        }
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

        // The same standard, on the other side: the prefix has to begin a simple selector app.css
        // actually declares, so a stylesheet that only talks about `.record-list` in a comment does not
        // satisfy this. Reading it with the same helper is the point — one definition of "declared".
        string[] declaredInStylesheet = SimpleSelectorsDeclaredIn(ReadStylesheet()).ToArray();
        string[] undeclared = SharedSelectorPrefixes
            .Where(prefix => !declaredInStylesheet.Any(
                selector => selector.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            $"{StylesheetRelativePath} declares no selector beginning {FormatList(undeclared)}, so this"
                + " test would pass on a tree with no shared vocabulary at all.");

        Assert.True(
            problems.Count == 0,
            "The §11.12 vocabulary is declared in app.css and nowhere else. Re-declared by:"
                + $" {FormatList(problems)}. A page-local block is for rules nobody else reads; a second"
                + " declaration of a shared name wins from later in the document at the same specificity,"
                + " so the stylesheet says one thing and the screen does another (F-66).");
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
            pagesChecked >= 7,
            $"Only {pagesChecked} pages render a record list. Slice 30 restructured four indexes and"
                + " Slice 34 added three more — a device roster, one sitting's bill, and one menu item's"
                + " history. Either the wrapper class was renamed without this test, or a page lost its"
                + " list.");

        Assert.True(
            problems.Count == 0,
            "A record-list cell without a data-label is a card cell with nothing saying what it holds,"
                + $" because overriding a table's display drops its header association: {FormatList(problems)}.");
    }

    /// <summary>
    /// The vocabulary §11.12's record list replaces has left the tree entirely.
    ///
    /// <para><b>This used to be a list, and emptying it is what Slice 34 finished.</b> The migration ran
    /// across three slices — four index pages in Slice 30, the two explorers in Slice 33, the two detail
    /// surfaces in Slice 34 — and for two of those the honest assertion was "these named files still hold
    /// one", compared for set equality so that neither the list nor the tree could quietly be wrong about
    /// the other. That arrangement exists to be dismantled: the moment the expected set is empty, the list
    /// is a name for zero things and F-47 says to delete it rather than keep it as a monument. What
    /// replaces it is stronger than what it asserted, because a <em>new</em> page reaching for the old
    /// shape now fails without anybody having to decide it should.</para>
    /// </summary>
    [Fact]
    public void TheRetiredTableVocabularyHasLeftTheTree()
    {
        int componentsScanned = 0;
        SortedSet<string> found = [];

        foreach (string path in EnumerateComponents())
        {
            // Comments are stripped, so a page explaining that it is *namespaced like* the retired
            // vocabulary is prose and not a use of it. Several pages say exactly that.
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            componentsScanned++;

            if (RetiredWrapperClasses.Any(name => markup.Contains(name, StringComparison.Ordinal)))
            {
                found.Add(Path.GetFileName(path));
            }
        }

        // Non-vacuity: an emptiness assertion over a walk that found nothing is the one failure mode a
        // list did not have, because a list had to be matched exactly (F-41).
        Assert.True(
            componentsScanned >= 20,
            $"Only {componentsScanned} components were scanned, so this fact is not looking at the tree it"
                + " is about — and unlike the list it replaced, an empty result is what it expects.");

        Assert.True(
            found.Count == 0,
            $"These pages carry the retired per-page table vocabulary: {FormatList(found)}. It left the"
                + " tree in M6 Slice 34, so this is a page reaching for the shape F-59 was about —"
                + " an eighty-line table declared inline, with the row's only affordance in a right-hand"
                + " column. §11.12's shared record list is what to reach for instead.");
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

    /// <summary>
    /// Every height a control declares is the one §11.12 states (<b>F-65</b>).
    ///
    /// <para><b>Why this is a finding and not a preference.</b> §11.12 names four kinds of control that
    /// carry <c>--touch-target</c> — buttons, links that act as buttons, checkbox rows, and the session
    /// links in the header — and <c>app.css</c> declared two of them at <c>2.25rem</c>. That is 36px
    /// against 44, and between them those two rules are "Sign out" in the header of every page in both
    /// layouts and the destructive action on four administration surfaces. The comment above one of them
    /// said it carried the §11.12 height and that it used "vertical padding rather than a min-height",
    /// above three lines that declared a <c>min-height</c> and no padding — so the file asserted its own
    /// compliance in prose while contradicting it in the declaration, which is exactly why no reader had
    /// caught it. Nothing measured it either: §16.3 scenario 16's reach selector covers a record row's
    /// action, a page-head action and a filter's submit, and none of those is ever a
    /// <c>.link-button</c>.</para>
    ///
    /// <para><b>What it asserts, in the form that does not report findings on correct trees.</b> A
    /// <c>min-height</c> is acceptable when it is <c>var(--touch-target)</c>, when it is exactly zero — an
    /// explicit opt-out, which is what a checkbox declares before its row carries the target instead — or
    /// when it is a length of at least 44px. A literal <em>under</em> the target is the finding; a literal
    /// <em>over</em> it is a page that wants more room and says so, which <c>KitchenBoard</c> does with a
    /// comment naming gloves, steam and a hurry (§11.2's wall-mounted kiosk). Values in viewport or
    /// percentage units are not control heights and are left alone, which is what <c>100dvh</c> on the
    /// app shell is.</para>
    ///
    /// <para>The literal is also F-48's mechanism in a stylesheet: <c>--touch-target</c> exists so the
    /// number is written once, and every literal beside it is a second copy waiting to disagree.</para>
    /// </summary>
    [Fact]
    public void EveryDeclaredControlHeightIsTheTouchTargetOrLarger()
    {
        List<string> problems = [];
        int declarationsRead = 0;
        int readFromTheProperty = 0;

        void Read(string css, string where)
        {
            foreach (Match match in MinimumHeightDeclaration.Matches(CssComment.Replace(css, string.Empty)))
            {
                string value = match.Groups[1].Value.Trim();
                declarationsRead++;

                if (value.Contains("--touch-target", StringComparison.Ordinal))
                {
                    readFromTheProperty++;
                    continue;
                }

                Match length = CssLength.Match(value);
                if (!length.Success)
                {
                    // Not a plain length: 100dvh on the app shell is a layout height, not a control's.
                    continue;
                }

                double magnitude = double.Parse(
                    length.Groups[1].Value, CultureInfo.InvariantCulture);
                double pixels = length.Groups[2].Value == "rem" ? magnitude * PixelsPerRem : magnitude;

                if (pixels == 0.0 || pixels >= MinimumTouchTargetPixels)
                {
                    continue;
                }

                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where} declares min-height: {value} ({pixels:0.#}px)"));
            }
        }

        Read(ReadStylesheet(), Path.GetFileName(StylesheetRelativePath));

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                Read(block.Groups[1].Value, Path.GetFileName(path));
            }
        }

        // Non-vacuity, both directions. A scan that read no declarations would report a clean tree having
        // looked at nothing, and a tree that had stopped reading the property at all would satisfy the
        // assertion below by writing 44px everywhere — which is the drift the property exists to stop.
        Assert.True(
            declarationsRead >= 10,
            $"Only {declarationsRead} min-height declaration(s) were found in the tree, and §11.12's"
                + " control rule is declared on well over ten. Either the scan is not reading the"
                + " stylesheets it is about or the rule has moved.");

        Assert.True(
            readFromTheProperty >= 5,
            $"Only {readFromTheProperty} min-height declaration(s) read --touch-target. The number is"
                + " supposed to be written once and referred to; a tree that had replaced every reference"
                + " with the literal 44px would pass the assertion below and have lost the arrangement.");

        string target = MinimumTouchTargetPixels.ToString("0", CultureInfo.InvariantCulture);

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} control(s) declare a height under §11.12's"
                + $" {target}px touch target: {FormatList(problems)}. Declare"
                + " var(--touch-target) rather than a literal — the property is where the number lives,"
                + " and a literal under it is a control a thumb misses (F-65). A control that wants MORE"
                + " room may say so with a larger length and a comment giving the reason.");
    }

    /// <summary>
    /// <c>overflow-wrap</c> is declared exactly once in the tree, on <c>body</c>, as <c>anywhere</c>.
    ///
    /// <para><b>Why this is a finding.</b> <c>overflow-wrap: anywhere</c> was declared eight times across
    /// the stylesheet, on the elements somebody had a long-token case in mind for. Because it is an
    /// <em>inherited</em> property, eight declarations are eight copies of what one declaration states,
    /// and the copies only reach the elements somebody thought of (F-48). The same display name rendered
    /// correctly on <c>/administration</c>, which had the rule, and wrongly on
    /// <c>/administration/people/{id}</c>, which did not — F-46 and F-51's shape again.</para>
    ///
    /// <para><c>anywhere</c> is asserted rather than <c>break-word</c> because only <c>anywhere</c>
    /// collapses min-content, and that is what §16.3 scenario 16's long-unbroken-name fixture depends on:
    /// <c>break-word</c> breaks the line and leaves the element's min-content width at the length of the
    /// token, so a table or flex context still sizes to it and the page still scrolls sideways.
    /// <c>word-break: break-all</c> on <c>.totp-secret</c> and the two join-code blocks is deliberately
    /// out of scope, being typesetting rather than overflow defence (F-41).</para>
    ///
    /// <para><b>Where this fact came from, which is the part that is worth writing down (F-70).</b> It
    /// arrived in the tree from outside this project's slice discipline: no row in the defect ledger, no
    /// paragraph in §16.4, no changelog entry, no line in <c>_CHANGES.md</c>. Everything above is
    /// reconstructed from the fact's own original comment and from what the tree shows, and the eight-times
    /// count is that comment's claim rather than something this repository can still demonstrate — the
    /// stylesheet it describes is two commits back. <b>The rule is right and it is kept.</b> What was
    /// missing was the paperwork, and it stayed missing because the number that would have exposed it was
    /// computed and then not read: Slice 34 predicted 1077 tests as arithmetic and the run returned 1078,
    /// and one unexplained test is one undocumented gate.</para>
    ///
    /// <para><b>Two repairs, both of the kind this file applies to everything else.</b> The component walk
    /// now carries a non-vacuity guard, because "declared exactly once in the tree" was satisfiable by a
    /// scan that read <c>app.css</c> and no component at all — the count would still have been one, and
    /// the assertion would have passed having looked at one file (F-41). And the value is asserted against
    /// the value rather than against the composed report line, which previously meant a repository path
    /// containing the word would have satisfied it.</para>
    /// </summary>
    [Fact]
    public void OverflowWrapIsDeclaredExactlyOnceOnTheBodyElement()
    {
        int blocksScanned = 0;
        List<string> declarations = [];
        List<string> values = [];

        void Read(string css, string where)
        {
            foreach (Match match in OverflowWrapDeclaration.Matches(CssComment.Replace(css, string.Empty)))
            {
                string value = match.Groups[1].Value.Trim();
                declarations.Add($"{where} ({value})");
                values.Add(value);
            }
        }

        string stylesheet = ReadStylesheet();
        Read(stylesheet, StylesheetRelativePath);

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                blocksScanned++;
                Read(block.Groups[1].Value, Path.GetFileName(path));
            }
        }

        // Non-vacuity, and it is the guard this fact arrived without: "exactly once in the tree" is
        // satisfied by app.css's one declaration whether or not a single component was ever opened.
        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so the word 'tree' in this fact's"
                + " name is not earned — app.css alone would satisfy the count below (F-41).");

        Assert.True(
            declarations.Count == 1,
            $"overflow-wrap must be declared exactly once in the tree and was found"
                + $" {declarations.Count} time(s): {FormatList(declarations)}. It is an inherited"
                + " property, so one declaration on body covers every element under it and a copy only"
                + " reaches the elements somebody remembered (F-48).");

        string only = declarations[0];

        Assert.True(
            only.StartsWith(StylesheetRelativePath, StringComparison.Ordinal),
            $"The one declaration belongs in {StylesheetRelativePath} and was found in {only}.");

        Assert.True(
            values[0].Contains("anywhere", StringComparison.Ordinal),
            $"overflow-wrap is declared as '{values[0]}' and §11.12 requires 'anywhere'. Only 'anywhere'"
                + " collapses min-content; 'break-word' breaks the line and leaves the element as wide as"
                + " its longest token, so a record card still pushes the page sideways — which is what"
                + " §16.3 scenario 16's unbroken display name is in the fixture to prove.");

        // On body specifically, not merely somewhere in app.css: an inherited property declared on a
        // descendant is the eight-copy arrangement with seven of the copies deleted.
        Match bodyRule = BodyRule.Match(CssComment.Replace(stylesheet, string.Empty));

        Assert.True(
            bodyRule.Success && bodyRule.Groups[1].Value.Contains("overflow-wrap", StringComparison.Ordinal),
            "overflow-wrap was found in app.css but not on the body rule. body is where an inherited"
                + " property is declared so that it reaches everything; anywhere else and it reaches"
                + " whatever happens to be inside that selector.");
    }

    /// <summary>
    /// Every colour the tree renders is a value <c>app.css</c>'s <c>:root</c> declares, and no reference
    /// to a property carries a fallback (<b>F-68</b>, <b>F-69</b>).
    ///
    /// <para><b>Why the palette has to be a rule and not a habit.</b> A duplicated colour does not fail.
    /// It drifts — and then one screen is a different red from every other screen and nobody can say
    /// which of the two anybody chose. Ninety-five colour literals were written outside <c>:root</c>:
    /// fifty inside <c>var()</c> fallbacks and forty-five bare, of which <b>twenty were byte-identical to
    /// a property declared in <c>:root</c></b>. <c>#ffffff</c> appeared six times against
    /// <c>--surface-raised</c>, three of them <em>inside <c>app.css</c> itself</em>; <c>#b45309</c> five
    /// times against <c>--caution-ink</c>; and <c>rgba(22, 32, 43, …)</c> three times, which is
    /// <c>--ink</c> in decimal and the one form no reader scanning for <c>#hex</c> would ever have
    /// seen.</para>
    ///
    /// <para><b>Seven had already drifted, and two of them are F-66 found in a fifth place.</b>
    /// <c>TableHistory</c>'s irreversible-hide warning — the one panel in the guest area whose whole job
    /// is to look alarming — drew <c>#fdecea</c> on <c>#f5c2c0</c> against the palette's
    /// <c>--danger-surface</c> <c>#fbeaea</c> and <c>--danger-hairline</c> <c>#f0c7c7</c>. That is the
    /// same pair of values Slice 34 removed from four <c>.chip-warn</c> copies, and it survived because
    /// the sweep that found those four was looking at administration and this is a guest page.
    /// <c>EventExplorer</c>'s five badge colours were all near-copies rather than exact ones, which is the
    /// harder half of the same defect: an exact copy is a duplicate, and a near copy is a decision nobody
    /// made.</para>
    ///
    /// <para><b>Why F-64's fact could not see any of this.</b> That one asserts that every property a rule
    /// <em>reads</em> is declared. A rule that reads nothing and writes <c>#b45309</c> is invisible to it.
    /// Same wrong-palette failure, direction reversed — and the reason the fallbacks are asserted in the
    /// same fact is that a fallback is what made F-64's undeclared names indistinguishable from declared
    /// ones for eight components. The fallback assertion comes first for that reason, and because it is
    /// the cheaper finding to clear.</para>
    ///
    /// <para><b>What it deliberately does not assert.</b> That a colour is the <em>right</em> colour;
    /// that two declared properties differ by more than a few bits; or that a component's <c>&lt;style&gt;</c>
    /// may not hold a rule of its own. A rule may be local. A colour may not.</para>
    /// </summary>
    [Fact]
    public void EveryColourTheTreeRendersIsDeclaredInThePalette()
    {
        string stylesheet = CssComment.Replace(ReadStylesheet(), string.Empty);

        Match palette = PaletteBlock.Match(stylesheet);

        Assert.True(
            palette.Success,
            $"No ':root' rule was found in {StylesheetRelativePath}. Every assertion below is about the"
                + " difference between the palette and everything else, so without it this fact would"
                + " report every colour in the file as a finding on a correct tree (F-41).");

        int coloursInThePalette = ColourLiteral.Matches(palette.Value).Count;

        Assert.True(
            coloursInThePalette >= 15,
            $"Only {coloursInThePalette} colour value(s) are declared in {StylesheetRelativePath}'s :root,"
                + " and the palette carries well over fifteen. Either the block has moved or the palette"
                + " has emptied, and in both cases the assertions below would be measuring nothing.");

        string outsideThePalette = stylesheet.Remove(palette.Index, palette.Length);

        int referencesRead = 0;
        int blocksScanned = 0;
        List<string> fallbacks = [];
        List<string> literals = [];

        void Read(string css, string where)
        {
            string stripped = CssComment.Replace(css, string.Empty);

            referencesRead += CustomPropertyReference.Matches(stripped).Count;

            foreach (Match match in CustomPropertyFallback.Matches(stripped))
            {
                fallbacks.Add($"{where}: {Line(stripped, match.Index)}");
            }

            // Declaration blocks only, so that an id selector is never read as a colour. `#ffffff` in a
            // value and `#blazor-error-ui` in a prelude are the same three characters of prefix, and a
            // gate that confused them would report a finding on a correct tree (F-41).
            foreach (string block in DeclarationBlocksIn(stripped))
            {
                foreach (Match match in ColourLiteral.Matches(block))
                {
                    literals.Add($"{where}: {match.Value} in '{Line(block, match.Index)}'");
                }
            }
        }

        Read(outsideThePalette, Path.GetFileName(StylesheetRelativePath));

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            foreach (Match block in StyleBlock.Matches(markup))
            {
                blocksScanned++;
                Read(block.Groups[1].Value, Path.GetFileName(path));
            }
        }

        // Non-vacuity, in both directions. A scan that found no component blocks would report a clean
        // tree having read one file, and a tree that had stopped referring to the palette at all would
        // satisfy the fallback assertion by having nothing to put a fallback on.
        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so this scan is not looking at the"
                + " tree it is about. Razor comments are stripped first — a <style> named inside an"
                + " @* … *@ comment is prose.");

        Assert.True(
            referencesRead >= 100,
            $"Only {referencesRead} var() reference(s) were read across the stylesheet and the component"
                + " blocks, and the tree makes over three hundred. A tree that had replaced its property"
                + " references with literals would satisfy the fallback assertion below trivially, having"
                + " lost the arrangement the property exists for.");

        Assert.True(
            fallbacks.Count == 0,
            $"{fallbacks.Count} var() reference(s) carry a fallback: {FormatList(fallbacks)}. Every name"
                + " the tree reads is declared — the fact above this one says so — which makes a fallback"
                + " dead code, and dead code in exactly the position where a misspelled name renders in"
                + " silence. That is how five properties came to be read fifty-five times and declared"
                + " nowhere (F-64), so the position is closed rather than watched (F-69).");

        Assert.True(
            literals.Count == 0,
            $"{literals.Count} colour value(s) are written outside the palette: {FormatList(literals)}."
                + " Every colour this application renders is declared once, in app.css's :root, and read"
                + " from there — because a second copy of a colour does not fail, it drifts, and then two"
                + " screens disagree and neither is the one anybody chose (F-68). If the value has no"
                + " property yet, declare one in :root and give it the name of the job it does.");
    }

    /// <summary>
    /// Every simple selector a stylesheet declares a rule for, comments removed.
    ///
    /// <para>Written by hand rather than with a CSS parser, and the shape is deliberate. Splitting on
    /// <c>{</c> gives one segment per rule; everything after that segment's last <c>}</c> is the next
    /// rule's prelude, and a declaration block contains no <c>{</c> so it never masquerades as one. An
    /// at-rule's prelude comes back too and is discarded by its leading <c>@</c> — but the rules
    /// <em>inside</em> it are found, which is what makes a shared name re-declared inside a media query
    /// visible to the caller. The prelude is then split on commas and on every combinator, so
    /// <c>.sitting-record .muted</c> yields both halves and a page overriding a shared name at higher
    /// specificity cannot hide behind an ancestor.</para>
    /// </summary>
    private static IEnumerable<string> SimpleSelectorsDeclaredIn(string css)
    {
        string[] segments = CssComment.Replace(css, string.Empty).Split('{');

        for (int index = 0; index < segments.Length - 1; index++)
        {
            string segment = segments[index];
            int closed = segment.LastIndexOf('}');
            string prelude = (closed >= 0 ? segment[(closed + 1)..] : segment).Trim();

            if (prelude.Length == 0 || prelude.StartsWith('@'))
            {
                continue;
            }

            foreach (string alternative in prelude.Split(','))
            {
                foreach (string simple in alternative.Split(
                    SelectorSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return simple.Trim();
                }
            }
        }
    }

    /// <summary>
    /// Every declaration block a stylesheet contains — the text between a rule's braces, comments
    /// already removed by the caller.
    ///
    /// <para>The mirror of <see cref="SimpleSelectorsDeclaredIn"/>, and it rests on the same property of
    /// CSS: a declaration block contains no <c>{</c>, so the text from an opening brace to the next
    /// closing brace is exactly one block's declarations. An at-rule wrapper is told apart by having
    /// another <c>{</c> before its own <c>}</c>, and is skipped — the loop reaches the rules nested
    /// inside it on its own, which is what makes a colour written inside the breakpoint query
    /// visible.</para>
    ///
    /// <para><b>Why the colour scan reads blocks rather than the whole file.</b> A prelude can contain an
    /// id selector, and <c>#blazor-error-ui</c> opens with the same character a hex colour does. Reading
    /// values only is what stops this fact reporting a finding on a correct tree (F-41).</para>
    ///
    /// <para><b>No <c>StringComparison</c> appears below, and that is the fix for F-71 rather than an
    /// oversight.</b> This method shipped calling <c>IndexOf('{', open + 1, StringComparison.Ordinal)</c>,
    /// an overload <c>System.String</c> declares for a <c>string</c> and <b>not</b> for a <c>char</c>: the
    /// char set is <c>IndexOf(char)</c>, <c>IndexOf(char, int)</c>, <c>IndexOf(char, StringComparison)</c>
    /// and <c>IndexOf(char, int, int)</c>, so argument three bound to <c>count</c> and the compiler
    /// reported a type mismatch on an argument rather than a missing member. This project did not compile
    /// for a slice, and because the failure was a build failure rather than a test failure, <c>dotnet
    /// test</c> printed <c>total: 497, failed: 0</c> while the five hundred-odd assertions in this project
    /// — including the two that slice had just written — never ran. Searching for a character is ordinal by
    /// construction, so nothing is lost: <c>IndexOf(char, StringComparison.Ordinal)</c> delegates to
    /// <c>IndexOf(char)</c> in the framework source, and <c>IndexOf(char, int)</c> delegates to
    /// <c>IndexOf(value, startIndex, Length - startIndex)</c>, which is the search intended here.</para>
    /// </summary>
    private static IEnumerable<string> DeclarationBlocksIn(string css)
    {
        for (int open = css.IndexOf('{'); open >= 0; open = css.IndexOf('{', open + 1))
        {
            int close = css.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            int nested = css.IndexOf('{', open + 1);
            if (nested >= 0 && nested < close)
            {
                // An at-rule wrapper. Its own "block" is a set of rules rather than declarations, and
                // each of those is reached by a later turn of this loop.
                continue;
            }

            yield return css[(open + 1)..close];
        }
    }

    /// <summary>
    /// The one line of <paramref name="text"/> that <paramref name="index"/> falls on, trimmed — so a
    /// failure message names the declaration rather than an offset.
    /// </summary>
    private static string Line(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        int end = text.IndexOf('\n', index);

        return (end < 0 ? text[start..] : text[start..end]).Trim();
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
