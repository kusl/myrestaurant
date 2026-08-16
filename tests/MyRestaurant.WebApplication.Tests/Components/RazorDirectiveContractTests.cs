using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

/// <summary>
/// No component reaches for a Razor directive this application does not use (TECHNICAL_SPECIFICATION
/// §16.4, <b>F-81</b>).
///
/// <para><b>Why this is a test.</b> Slice 40 named two loop variables <c>section</c> — one on the
/// create-item form, one on the guest ordering surface — and wrote <c>@@section.MenuSectionName</c>. That
/// is not a member access. <c>@@section</c> is the MVC <em>section directive</em>, a reserved word in
/// Razor's own grammar, so the parser read a directive with a malformed name and the build produced four
/// errors across two files: <c>RZ9979</c>, <c>RZ2005</c> and <c>RZ1011</c>, none of which mentions an
/// identifier and none of which is about the markup. The neighbouring <c>@@key="section.…"</c> and
/// <c>@@SectionHeadingId(section)</c> on the same two files compiled perfectly, because neither puts the
/// word directly after an <c>@@</c> — which is exactly what made the errors look like they were about the
/// element rather than the name.</para>
///
/// <para><b>Why a rule rather than a memory (F-47).</b> The obvious response is to remember not to call a
/// variable <c>section</c>, and this project has already recorded what happens to rules kept that way. It
/// is decidable from text with certainty: Blazor has no sections at all — <c>@@section</c> and
/// <c>@@RenderSection</c> belong to MVC and Razor Pages layouts, this application has neither, and
/// <c>App.razor</c> composes its layout with components. So the honest assertion is total: <b>the token
/// does not appear in this tree</b>, and a variable, parameter or member whose name collides with it is
/// caught at unit-test time with a message naming the file and the line rather than at
/// <c>dotnet build</c> with a message naming a directive nobody wrote.</para>
///
/// <para><b>What it deliberately does not do.</b> Enumerate Razor's whole directive vocabulary and check
/// each against the tree. Most directives are ones this application uses on purpose — <c>@@page</c>,
/// <c>@@inject</c>, <c>@@using</c>, <c>@@attribute</c> — and a gate with an opinion about which of those
/// were allowed would be a gate about style. What is asserted is the pair that are unusable here by
/// construction, so the rule is a consequence of the framework rather than of anybody's preference. A
/// third arrives the day this application grows an MVC layout, and on that day this file is where the
/// exemption is written down.</para>
///
/// <para>Razor comments are stripped before anything is read, on the standard every other component scan
/// in this directory applies: three files now explain this finding in an <c>@@* … *@</c> block, and a gate
/// that could not tell a sentence about a token from a use of one would report findings on the very files
/// that record the fix (F-67's lesson, one register over).</para>
/// </summary>
public sealed class RazorDirectiveContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>
    /// The directives MVC and Razor Pages define and Blazor does not implement. Both are unusable in this
    /// tree by construction rather than by policy, which is what makes an absolute rule about them
    /// something other than an opinion.
    /// </summary>
    private static readonly string[] UnusableDirectives = ["section", "RenderSection"];

    /// <summary>A Razor server-side comment, stripped before anything else is read.</summary>
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    /// <summary>
    /// A directive token in transition-position: an <c>@@</c> that is not itself escaped, followed by the
    /// word.
    ///
    /// <para>The negative look-behind is what keeps this off correct trees (F-41). <c>@@@@section</c> is
    /// Razor's escape for a literal at-sign and renders as text; several comment blocks in this tree write
    /// it that way when quoting an attribute, and the surrounding comment is stripped before this runs —
    /// but the same escape is legitimate in ordinary markup, so it is excluded by the pattern rather than
    /// by luck. The word-boundary on the right is what stops <c>@@sectionHeading</c> — a perfectly
    /// ordinary member — being reported.</para>
    /// </summary>
    private static Regex DirectiveUse(string directive)
        => new($@"(?<!@)@{Regex.Escape(directive)}\b");

    /// <summary>
    /// No component uses a directive Blazor does not implement — which, because the parser claims the
    /// token before C# ever sees it, is also the rule that no identifier may be named after one.
    /// </summary>
    [Fact]
    public void NoComponentReachesForADirectiveThisApplicationCannotUse()
    {
        int componentsScanned = 0;
        List<string> problems = [];

        foreach (string path in EnumerateComponents())
        {
            componentsScanned++;

            string markup = RazorComment.Replace(File.ReadAllText(path), string.Empty);
            string[] lines = markup.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                foreach (string directive in UnusableDirectives)
                {
                    if (DirectiveUse(directive).IsMatch(lines[index]))
                    {
                        problems.Add($"{Path.GetFileName(path)}:{index + 1} writes '@{directive}'");
                    }
                }
            }
        }

        // Non-vacuity. An emptiness assertion over a walk that opened nothing is the one failure mode
        // this shape has, and it is the failure every computed-subject gate in this tree guards (F-41).
        Assert.True(
            componentsScanned >= 20,
            $"Only {componentsScanned} components were scanned, so this fact is not looking at the tree"
                + " it is about — and unlike a list, an empty result is what it expects.");

        Assert.True(
            problems.Count == 0,
            $"A component writes a directive Blazor does not implement: {FormatList(problems)}. If this"
                + " is a variable or a member rather than a directive, rename it: Razor's parser claims"
                + " the token in transition-position before C# ever sees the expression, so the build"
                + " fails with RZ9979/RZ2005/RZ1011 naming a directive nobody wrote and saying nothing"
                + " about the identifier (F-81). `menuSection` is what the two files that hit this now"
                + " use. If Razor sections have genuinely become usable here, this is the file that"
                + " should record the exemption.");
    }

    /// <summary>
    /// The scan can see a use, which is the half an emptiness assertion cannot demonstrate about itself.
    ///
    /// <para><b>Proven against synthesised markup rather than against the tree</b>, and the distinction is
    /// the point: this fact must stay true after the repair, so it cannot depend on the tree still
    /// containing the defect. Three shapes are checked in both directions — the use that failed the build,
    /// the escaped literal that is correct, and the longer identifier that merely starts with the same
    /// letters. F-64, F-67 and F-68 each began as an assertion that was true and could not have detected
    /// its own subject.</para>
    /// </summary>
    [Fact]
    public void TheScanTellsADirectiveFromAnEscapeAndFromALongerName()
    {
        Regex section = DirectiveUse("section");

        // The exact line that produced RZ1011 on TableOrderSurface.razor, and the one from
        // CreateMenuItem.razor. Both must be reported, or the fact above is decoration.
        Assert.Matches(section, "                @section.MenuSectionName");
        Assert.Matches(section, "<option value=\"@section.MenuSectionIdentifier\">");

        // Razor's escape for a literal at-sign. Legitimate anywhere in markup, and reporting it would be
        // a finding on a correct tree.
        Assert.DoesNotMatch(section, "the `@@section` directive is MVC's");

        // A longer identifier that merely begins with the word, and the same word with no transition in
        // front of it — both perfectly ordinary and both outside this rule.
        Assert.DoesNotMatch(section, "@sectionHeadingId(item)");
        Assert.DoesNotMatch(section, "<div class=\"order-menu-section\" @key=\"menuSection.Name\">");

        // And the sibling directive, so the second entry in the list is not carried untested.
        Assert.Matches(DirectiveUse("RenderSection"), "@RenderSection(\"Scripts\", required: false)");
    }

    private static IEnumerable<string> EnumerateComponents()
        => Directory.EnumerateFiles(PathTo(ComponentsRelativePath), "*.razor", SearchOption.AllDirectories);

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
