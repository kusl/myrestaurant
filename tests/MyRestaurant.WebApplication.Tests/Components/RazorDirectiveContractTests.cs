using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

public sealed class RazorDirectiveContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private static readonly string[] UnusableDirectives = ["section", "RenderSection"];

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    private static Regex DirectiveUse(string directive)
        => new($@"(?<!@)@{Regex.Escape(directive)}\b");

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

    [Fact]
    public void TheScanTellsADirectiveFromAnEscapeAndFromALongerName()
    {
        Regex section = DirectiveUse("section");

        Assert.Matches(section, "                @section.MenuSectionName");
        Assert.Matches(section, "<option value=\"@section.MenuSectionIdentifier\">");

        Assert.DoesNotMatch(section, "the `@@section` directive is MVC's");

        Assert.DoesNotMatch(section, "@sectionHeadingId(item)");
        Assert.DoesNotMatch(section, "<div class=\"order-menu-section\" @key=\"menuSection.Name\">");

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
