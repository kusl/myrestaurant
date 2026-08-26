using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

public sealed class HandheldLayoutContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string StylesheetRelativePath = "src/MyRestaurant.WebApplication/wwwroot/app.css";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private static readonly string[] SharedSelectorPrefixes =
    [
        ".record-",
        ".page-head",
        ".filter-",
        ".manage-",
        ".menu-group",
        ".chip",
        ".muted",
        ".visually-hidden",
    ];

    private static readonly string[] RetiredWrapperClasses =
        ["admin-people", "admin-tables", "admin-menu", "admin-sittings", "admin-row-actions", "admin-header"];

    private const double MinimumTouchTargetPixels = 44.0;

    private const double PixelsPerRem = 16.0;

    private static readonly string[] AdministrationAreaPaths =
    [
        "/administration",
        "/administration/tables",
        "/administration/menu",
        "/administration/sittings",
        "/administration/hidden-records",
        "/administration/events",
    ];

    private const string AreaLinksComponentFileName = "AdministrationAreaLinks.razor";

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    private static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    private static readonly Regex StyleBlock = new(@"<style>(.*?)</style>", RegexOptions.Singleline);

    private static readonly Regex MediaQuery = new(@"@media([^{]*)\{");

    private static readonly Regex CustomPropertyDeclaration = new(@"(?m)^\s*(--[a-z0-9-]+)\s*:");

    private static readonly Regex MinimumHeightDeclaration = new(@"min-height\s*:\s*([^;}]+)");

    private static readonly Regex CssLength = new(@"^([0-9]*\.?[0-9]+)(rem|px)$");

    private static readonly char[] SelectorSeparators = [' ', '\t', '\n', '\r', '>', '+', '~'];

    private static readonly Regex CustomPropertyReference = new(@"var\(\s*(--[a-z0-9-]+)");

    private static readonly Regex LiteralHref = new(@"href=""(/[^""@]*)""");

    private static readonly Regex PageDirective = new(@"(?m)^@page\s+""([^""]+)""");

    private static readonly Regex ColourLiteral =
        new(@"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|hsla?\([^)]*\)");

    private static readonly Regex PaletteBlock = new(@":root\s*\{[^{}]*\}");

    private static readonly Regex CustomPropertyFallback = new(@"var\(\s*--[a-z0-9-]+\s*,");

    private static readonly Regex OverflowWrapDeclaration = new(@"overflow-wrap\s*:\s*([^;}]+)");

    private static readonly Regex BodyRule = new(@"(?m)^body\s*\{([^}]+)\}");

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

        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so the F-63 half of this fact is"
                + " not looking at the tree it is about. Razor comments are stripped first — a <style>"
                + " mentioned inside an @* … *@ comment is prose.");

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

        string condition48 = only[(only.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        int blockStart = stylesheet.IndexOf(condition48, StringComparison.Ordinal);
        string tail = stylesheet[blockStart..];
        Assert.True(
            tail.Count(character => character == ';') >= 10,
            $"{StylesheetRelativePath}'s breakpoint block looks empty. A query that widens nothing is a"
                + " rule satisfied by deleting the wide layout.");
    }

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

        Assert.True(
            blocksScanned >= 8,
            $"Only {blocksScanned} component <style> blocks were found, so this scan is not looking at"
                + " the tree it is about. Razor comments are stripped first — a <style> mentioned inside"
                + " an @* … *@ comment is prose, and counting it would make this guard pass on nothing.");

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

    [Fact]
    public void TheRetiredTableVocabularyHasLeftTheTree()
    {
        int componentsScanned = 0;
        SortedSet<string> found = [];

        foreach (string path in EnumerateComponents())
        {
            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);

            componentsScanned++;

            if (RetiredWrapperClasses.Any(name => markup.Contains(name, StringComparison.Ordinal)))
            {
                found.Add(Path.GetFileName(path));
            }
        }

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

        Match bodyRule = BodyRule.Match(CssComment.Replace(stylesheet, string.Empty));

        Assert.True(
            bodyRule.Success && bodyRule.Groups[1].Value.Contains("overflow-wrap", StringComparison.Ordinal),
            "overflow-wrap was found in app.css but not on the body rule. body is where an inherited"
                + " property is declared so that it reaches everything; anywhere else and it reaches"
                + " whatever happens to be inside that selector.");
    }

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
                continue;
            }

            yield return css[(open + 1)..close];
        }
    }

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
