using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

public sealed class TestingSectionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";
    private const string TestsRelativePath = "tests";

    private const string SectionOpening = "**16.4 CI:**";

    private const int MinimumCountedClasses = 37;

    private static readonly Regex CitedTestClass = new(@"`[^`]*?([A-Za-z0-9_]+Tests\.cs)`");

    private static readonly Regex AssertionCount = new(@"\b([A-Za-z]+|[0-9]+)\s+assertions?\b");

    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six",
        "seven", "eight", "nine", "ten", "eleven", "twelve",
    ];

    private static readonly Regex TestAttribute = new(@"\[(Fact|Theory)\b");

    [Fact]
    public void SectionSixteenFourCountsWhatTheContractTestsHold()
    {
        string section = ReadTestingSection();

        Dictionary<string, string> testFilesByName = TestFilesByName();

        List<string> ambiguous = [];
        List<string> uncited = [];
        List<string> disagreements = [];
        List<string> counted = [];

        foreach (string paragraph in section.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] names = CitedTestClass.Matches(paragraph)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Length == 0)
            {
                continue;
            }

            int[] claims = AssertionCount.Matches(paragraph)
                .Select(match => ValueOf(match.Groups[1].Value))
                .Where(value => value >= 0)
                .ToArray();

            if (names.Length > 1 && claims.Length > 0)
            {
                ambiguous.Add(
                    $"a paragraph names {names.Length} test classes ({FormatList(names)}) and states"
                        + $" {claims.Length} count(s), so no claim can be attributed to a file");
                continue;
            }

            if (names.Length > 1)
            {
                continue;
            }

            string name = names[0];

            if (!testFilesByName.TryGetValue(name, out string? path))
            {
                uncited.Add($"§16.4 names {name}, which is not a file under {TestsRelativePath}/");
                continue;
            }

            if (claims.Length == 0)
            {
                continue;
            }

            if (claims.Length > 1)
            {
                ambiguous.Add(
                    $"the paragraph for {name} states {claims.Length} assertion counts"
                        + $" ({string.Join(", ", claims.Select(claim => claim.ToString(CultureInfo.InvariantCulture)))}),"
                        + " so it is not decidable which one is the claim");
                continue;
            }

            int claimed = claims[0];
            int held = TestAttribute.Matches(File.ReadAllText(path)).Count;

            counted.Add(name);

            if (claimed != held)
            {
                disagreements.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"§16.4 says {claimed} for {name}, which holds {held}"));
            }
        }

        Assert.True(
            uncited.Count == 0,
            $"§16.4 cites a test class that is not in the tree: {FormatList(uncited)}. A citation that"
                + " resolves to nothing is worse than none, because a reader follows it and concludes the"
                + " section is stale rather than that one name is.");

        Assert.True(
            ambiguous.Count == 0,
            $"§16.4 states an assertion count that cannot be attributed to one file:"
                + $" {FormatList(ambiguous)}. The comparison below needs one class and one number per"
                + " paragraph; anything else is skipped, and a skipped paragraph is how a gate stops"
                + " reaching the thing it is about.");

        Assert.True(
            counted.Count >= MinimumCountedClasses,
            $"Only {counted.Count} test class(es) in §16.4 state an assertion count, and at least"
                + $" {MinimumCountedClasses} do: {FormatList(counted)}. Either the section marker"
                + $" '{SectionOpening}' has moved, or a paragraph stopped saying how many assertions its"
                + " class holds — and the second is how this comparison quietly stops being made.");

        Assert.True(
            disagreements.Count == 0,
            $"{disagreements.Count} of §16.4's assertion counts disagree with the file:"
                + $" {FormatList(disagreements)}. A count in prose is the same fact written a second time"
                + " and the two drift — F-48, F-50, F-56 and F-65 are each an instance. When they drift,"
                + " what is usually true is that an assertion landed and the document was not told"
                + " (F-70): describe it in §16.4, give it a row in DOCUMENTATION_REVIEW.md if it closes a"
                + " finding, and move the count.");
    }

    private static string ReadTestingSection()
    {
        string specification = File.ReadAllText(PathTo(SpecificationRelativePath));

        int start = specification.IndexOf(SectionOpening, StringComparison.Ordinal);

        Assert.True(
            start >= 0,
            $"'{SectionOpening}' was not found in {SpecificationRelativePath}, so this test has nothing"
                + " to read. The marker is how the section is located; if it was reworded, reword it here"
                + " too rather than leaving a gate that passes on an empty string.");

        int end = specification.IndexOf("\n## ", start, StringComparison.Ordinal);

        return end < 0 ? specification[start..] : specification[start..end];
    }

    private static Dictionary<string, string> TestFilesByName()
    {
        Dictionary<string, List<string>> found = [];

        foreach (string path in Directory.EnumerateFiles(
            PathTo(TestsRelativePath), "*Tests.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path);

            if (!found.TryGetValue(name, out List<string>? paths))
            {
                paths = [];
                found[name] = paths;
            }

            paths.Add(path);
        }

        Dictionary<string, string> unique = [];

        foreach ((string name, List<string> paths) in found)
        {
            if (paths.Count == 1)
            {
                unique[name] = paths[0];
            }
        }

        Assert.True(
            unique.Count >= 20,
            $"Only {unique.Count} uniquely named test class(es) were found under {TestsRelativePath}/,"
                + " and this tree has well over twenty. The walk is not reading the project it is about,"
                + " and every citation below would be reported as unresolvable on a correct tree (F-41).");

        return unique;
    }

    private static int ValueOf(string token)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int digits))
        {
            return digits;
        }

        int word = Array.FindIndex(
            NumberWords,
            candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase));

        return word;
    }

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }

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
