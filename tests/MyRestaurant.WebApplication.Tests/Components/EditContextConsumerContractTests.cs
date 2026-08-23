using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

/// <summary>
/// Every component that consumes an <c>EditContext</c> sits inside the <c>EditForm</c> that supplies one
/// (TECHNICAL_SPECIFICATION §16.4, <b>F-106</b>).
///
/// <para><b>Why this is a test, and it is the worst symptom this repository has shipped.</b> Slice 53 put
/// <c>&lt;ValidationMessage For="@(() =&gt; AltTextInput.AltText)" /&gt;</c> one line BELOW
/// <c>&lt;/EditForm&gt;</c> on <c>ManageMenuItem.razor</c>. <c>ValidationMessage</c> takes its
/// <c>EditContext</c> from a cascading value an <c>EditForm</c> supplies to its <em>children</em>, and a
/// sibling is not a child — so <c>OnParametersSet</c> throws <c>InvalidOperationException</c>. Not an
/// empty message: an unhandled exception during render, which is <b>HTTP 500</b>.</para>
///
/// <para><b>The timing is what made it invisible and what made it severe.</b> The offending markup sat
/// inside <c>@if (_picture is not null)</c>. On the POST that attaches a picture,
/// <c>OnInitializedAsync</c> has already run and <c>_picture</c> is still <c>null</c>, so the block does
/// not render, the handler runs, the row commits and the redirect is issued — the upload SUCCEEDS. The
/// GET it redirects to is the first render in which a picture exists, and it answers 500. From then on
/// every administrator view of that item answered 500, <em>including the one carrying the Remove
/// button</em>, so the state was not reversible from any surface in the application. Reported by the
/// operator as “when I try to upload an image I get a 500”, which is exactly what it looks like from
/// outside and names the wrong request.</para>
///
/// <para><b>Nothing in the suite could see it, and the reason generalises.</b>
/// <c>MenuItemImageSurfaceContractTests</c> reads that very file, and reads it as text — it asserted the
/// caption form has no file input and never had an opinion about what is inside the form versus beside
/// it. No §16.3 scenario had ever uploaded a picture (that gap is closed in the same slice as this file).
/// This repository has no bUnit by §16.1, so no test renders a component, and the compiler cannot help:
/// the placement is legal Razor and legal C#. The defect is a property of the <em>markup's nesting</em>,
/// which is decidable from text with certainty, so that is what is asserted.</para>
///
/// <para><b>The rule is a consequence of the framework rather than of anybody's taste</b>, which is the
/// standard <c>RazorDirectiveContractTests</c> sets for an absolute rule in this directory. All three
/// components below require the cascading <c>EditContext</c> and all three throw without it; there is no
/// arrangement in which one outside a form is correct, and none in which the framework would tell you so
/// before a browser did.</para>
///
/// <para>Razor comments are stripped before anything is read, on the standard every component scan in
/// this directory applies — three files now discuss this finding in <c>@* … *@</c> blocks, and a gate
/// that could not tell a sentence about a tag from a use of one would report findings on the very files
/// that record the fix (F-67's lesson, one register over).</para>
///
/// <para>Pure: reads files off the disk it was built from. No server, no container, no browser.</para>
/// </summary>
public sealed class EditContextConsumerContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>
    /// The framework components that require the cascading <see cref="Microsoft.AspNetCore.Components.Forms.EditContext"/>
    /// and throw from <c>OnParametersSet</c> without it.
    ///
    /// <para>Deliberately these three and no others. <c>InputText</c>, <c>InputNumber</c> and the rest of
    /// the <c>InputBase</c> family are <em>not</em> here: they tolerate a missing context in some
    /// arrangements and, more to the point, a bound input outside its form is a different defect with a
    /// different symptom. What is asserted is the set that turns a placement mistake into a 500, so a
    /// failure here always means the same thing.</para>
    /// </summary>
    private static readonly string[] ContextConsumers =
    [
        "ValidationMessage",
        "ValidationSummary",
        "DataAnnotationsValidator",
    ];

    /// <summary>A Razor server-side comment, stripped before anything else is read.</summary>
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    /// <summary>
    /// An element tag, captured as (closing slash, name). Deliberately a scan for tags rather than a
    /// parse of the document: these files are Razor, and a parser would have to be taught about
    /// <c>@</c> expressions, directive attributes and code blocks to say anything this cannot.
    /// </summary>
    private static readonly Regex Tag = new(@"<(/?)([A-Za-z][A-Za-z0-9]*)");

    /// <summary>
    /// No component consumes an <c>EditContext</c> from outside the <c>EditForm</c> that supplies one.
    /// </summary>
    [Fact]
    public void EveryEditContextConsumerIsInsideItsEditForm()
    {
        int componentsScanned = 0;
        int consumersFound = 0;
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            componentsScanned++;

            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);
            string name = Path.GetFileName(path);

            foreach (Finding finding in Walk(markup))
            {
                consumersFound++;

                if (finding.Depth == 0)
                {
                    problems.Add(string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{name}:{finding.Line} <{finding.Component}> is not inside an <EditForm>"));
                }
            }
        }

        // Non-vacuity, both halves. A walk that opened nothing, and a walk that opened everything and
        // found no consumer, would each pass an emptiness assertion by having nothing to judge (F-41).
        Assert.True(
            componentsScanned >= 20,
            $"Only {componentsScanned} components were scanned, so this fact is not looking at the tree"
                + " it is about — and unlike a list, an empty result is what it expects.");

        Assert.True(
            consumersFound >= 5,
            $"Only {consumersFound} EditContext consumer(s) were found across {componentsScanned}"
                + " components, which is fewer than this tree has. The scan is not reaching them, so an"
                + " assertion that none is misplaced is an assertion about nothing.");

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} component(s) consume an EditContext from outside an <EditForm>:"
                + $" {FormatList(problems)}. An EditForm cascades its EditContext to its CHILDREN, so a"
                + " sibling gets none and ValidationMessage/ValidationSummary/DataAnnotationsValidator"
                + " each throw InvalidOperationException from OnParametersSet — which is a 500 on the"
                + " page rather than a missing sentence, and it fires only on the renders where the"
                + " surrounding markup is actually emitted (F-106). Move the tag inside the form.");
    }

    /// <summary>
    /// The walk can tell inside from outside, which is the half an emptiness assertion cannot demonstrate
    /// about itself.
    ///
    /// <para><b>Proven against synthesised markup rather than against the tree</b>, and the distinction is
    /// the point: the fact above must stay true after the repair, so it cannot depend on the tree still
    /// containing the defect. The first case below is <c>ManageMenuItem.razor</c>'s exact shape as it
    /// shipped in Slice 53 — the form, its close tag, then the message — and the second is the same
    /// markup repaired. F-64, F-67 and F-68 each began as an assertion that was true and could not have
    /// detected its own subject.</para>
    /// </summary>
    [Fact]
    public void TheWalkTellsASiblingFromAChildAndSurvivesNesting()
    {
        const string asShipped = """
            <EditForm Model="AltTextInput" FormName="menu-item-image-alt-text">
                <DataAnnotationsValidator />
                <button type="submit">Save caption</button>
            </EditForm>

            <ValidationMessage For="@(() => AltTextInput.AltText)" />
            """;

        const string repaired = """
            <EditForm Model="AltTextInput" FormName="menu-item-image-alt-text">
                <DataAnnotationsValidator />
                <button type="submit">Save caption</button>
                <ValidationMessage For="@(() => AltTextInput.AltText)" />
            </EditForm>
            """;

        Finding[] shipped = [.. Walk(asShipped)];
        Assert.Equal(2, shipped.Length);
        Assert.Contains(shipped, finding => finding.Component == "DataAnnotationsValidator" && finding.Depth == 1);
        Assert.Contains(shipped, finding => finding.Component == "ValidationMessage" && finding.Depth == 0);

        Assert.All(Walk(repaired), finding => Assert.Equal(1, finding.Depth));

        // A self-closed EditForm supplies a context to nothing and must not be counted as open, and an
        // ordinary element whose name merely begins with the same letters is not an EditForm at all.
        // Both are shapes a substring-minded scan gets wrong, and getting either wrong would put a
        // genuinely misplaced consumer at depth 1 and report nothing.
        Assert.Equal(0, Assert.Single(Walk("<EditForm /><ValidationMessage />")).Depth);
        Assert.Equal(
            0,
            Assert.Single(Walk("<EditFormFooter><ValidationMessage /></EditFormFooter>")).Depth);

        // A consumer between two forms belongs to neither, which is the arrangement a page with several
        // editors actually has — ManageMenuItem.razor carries six.
        Finding[] between = [.. Walk("<EditForm></EditForm><ValidationSummary /><EditForm></EditForm>")];
        Assert.Equal(0, Assert.Single(between).Depth);
    }

    /// <summary>
    /// Every <c>EditContext</c> consumer in <paramref name="markup"/>, each with the number of
    /// <c>EditForm</c> elements open around it. Depth rather than a boolean because nesting is legal
    /// markup even where this tree has none, and a walk that answered yes/no would have to decide what an
    /// unbalanced document means instead of reporting it.
    /// </summary>
    private static IEnumerable<Finding> Walk(string markup)
    {
        string text = markup.Replace("\r\n", "\n", StringComparison.Ordinal);
        int depth = 0;

        foreach (Match match in Tag.Matches(text))
        {
            string name = match.Groups[2].Value;
            bool closing = match.Groups[1].Value.Length > 0;

            if (name == "EditForm")
            {
                if (closing)
                {
                    // Clamped at zero rather than going negative: an unbalanced document is a defect of
                    // its own, and letting the count go under would make the NEXT consumer on the page
                    // look correct, which is the direction a gate must never fail in.
                    depth = Math.Max(0, depth - 1);
                }
                else if (!IsSelfClosed(text, match.Index))
                {
                    depth++;
                }

                continue;
            }

            if (closing || Array.IndexOf(ContextConsumers, name) < 0)
            {
                continue;
            }

            yield return new Finding(name, depth, LineOf(text, match.Index));
        }
    }

    /// <summary>
    /// Whether the tag opening at <paramref name="index"/> ends in <c>/&gt;</c>. A self-closed
    /// <c>&lt;EditForm /&gt;</c> supplies a context to nothing, so counting it as open would put every
    /// consumer after it inside a form that has no children.
    /// </summary>
    private static bool IsSelfClosed(string text, int index)
    {
        int close = text.IndexOf('>', index);
        return close > index && text[close - 1] == '/';
    }

    private static int LineOf(string text, int index)
        => text.AsSpan(0, index).Count('\n') + 1;

    private sealed record Finding(string Component, int Depth, int Line);

    private static IEnumerable<string> EnumerateComponents()
        => Directory
            .EnumerateFiles(PathTo(ComponentsRelativePath), "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

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
    /// The same walk up to <c>MyRestaurant.slnx</c> every other contract test in this directory uses, and
    /// it throws rather than skips for the same reason: a check that quietly declines to run is worse
    /// than none.
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

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }
}
