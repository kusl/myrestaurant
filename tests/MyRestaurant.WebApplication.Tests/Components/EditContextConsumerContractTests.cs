using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

public sealed class EditContextConsumerContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private static readonly string[] ContextConsumers =
    [
        "ValidationMessage",
        "ValidationSummary",
        "DataAnnotationsValidator",
    ];

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline);

    private static readonly Regex Tag = new(@"<(/?)([A-Za-z][A-Za-z0-9]*)");

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

        Assert.Equal(0, Assert.Single(Walk("<EditForm /><ValidationMessage />")).Depth);
        Assert.Equal(
            0,
            Assert.Single(Walk("<EditFormFooter><ValidationMessage /></EditFormFooter>")).Depth);

        Finding[] between = [.. Walk("<EditForm></EditForm><ValidationSummary /><EditForm></EditForm>")];
        Assert.Equal(0, Assert.Single(between).Depth);
    }

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
