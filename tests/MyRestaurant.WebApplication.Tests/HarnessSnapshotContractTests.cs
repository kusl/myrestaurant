using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class HarnessSnapshotContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string HarnessRelativePath = "tests/MyRestaurant.EndToEnd.Tests/Harness";

    private const int MinimumHarnessFiles = 15;

    private const int MinimumPredicateSubjects = 2;

    private const int MinimumReaders = 4;

    private const int MinimumBodyBytes = 400;

    private static readonly Regex HarnessRecord = new(@"internal sealed record (\w+)\(");

    private static readonly Regex PredicateParameter = new(@"Func<(\w+)\??,\s*bool>");

    private static readonly Regex Reader = new(@"\bTask<(\w+)\??>\s+(\w+)\(");

    private static readonly string[] BrowserReads =
    [
        "GetAttributeAsync(",
        "InnerTextAsync(",
        "TextContentAsync(",
        "AllTextContentsAsync(",
        "InputValueAsync(",
        "IsCheckedAsync(",
        "IsVisibleAsync(",
        "CountAsync(",
        "EvaluateAsync(",
        "DeclaredAsync(",
    ];

    private const string TornFixture = """
        internal sealed record Reading(int First, int Second);

        internal static class Surface
        {
            internal static async Task<Reading> ReadAsync(IPage page)
                => new Reading(
                    await page.Locator("a").CountAsync(),
                    await page.Locator("b").CountAsync());

            internal static async Task<Reading> WaitAsync(IPage page, Func<Reading, bool> expectation)
            {
                Reading observed = await ReadAsync(page);

                while (!expectation(observed))
                {
                    observed = await ReadAsync(page);
                }

                return observed;
            }
        }
        """;

    private const string WholeFixture = """
        internal sealed record Reading(int First, int Second);

        internal static class Surface
        {
            internal static async Task<Reading> ReadAsync(IPage page)
            {
                JsonElement? evaluated = await page.EvaluateAsync(Script, Selectors);

                return new Reading(evaluated!.Value[0].GetInt32(), evaluated!.Value[1].GetInt32());
            }

            internal static async Task<Reading> WaitAsync(IPage page, Func<Reading, bool> expectation)
            {
                Reading observed = await ReadAsync(page);

                while (!expectation(observed))
                {
                    observed = await ReadAsync(page);
                }

                return observed;
            }
        }
        """;

    private const string UnpolledFixture = """
        internal sealed record Reading(int First, int Second);

        internal static class Surface
        {
            internal static async Task<Reading> ReadAsync(IPage page)
                => new Reading(
                    await page.Locator("a").CountAsync(),
                    await page.Locator("b").CountAsync());
        }
        """;

    [Fact]
    public void EveryCompositeAPredicateIsAskedAboutIsReadInOneEvaluation()
    {
        HarnessSource[] harness = ReadHarness();

        Assert.True(
            harness.Length >= MinimumHarnessFiles,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {harness.Length} file(s) were read from {HarnessRelativePath}/ and it holds at"
                + $" least {MinimumHarnessFiles}. Every finding below is an absence, and an absence over"
                + $" a walk that opened nothing is the vacuous pass F-41 prohibits."));

        Dictionary<string, bool> composites = CompositesIn(harness);
        HashSet<string> subjects = PredicateSubjectsIn(harness, composites);

        Assert.True(
            subjects.Count >= MinimumPredicateSubjects,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {subjects.Count} composite(s) in this harness are the subject of a"
                + $" `Func<T, bool>` and at least {MinimumPredicateSubjects} are, so the subject set"
                + $" this rule computes has gone quiet. Either a waiting verb stopped taking a"
                + $" predicate, or `internal sealed record` stopped being how these are declared —"
                + $" and either way nothing below is being checked."));

        Reading[] readings = ReadingsIn(harness, subjects);

        Assert.True(
            readings.Length >= MinimumReaders,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {readings.Length} method(s) return one of {Join(subjects)} and at least"
                + $" {MinimumReaders} do. The signature scan is not reaching the methods it is about."));

        long bodyBytes = readings.Sum(reading => (long)reading.Body.Length);

        Assert.True(
            bodyBytes >= MinimumBodyBytes,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {readings.Length} bodies found total {bodyBytes} byte(s), under"
                + $" {MinimumBodyBytes}. Bodies are delimited by brace matching, which a stray brace"
                + $" inside a string literal would truncate — and a truncated body contains no browser"
                + $" read, so it passes. This floor is what says the extraction still works."));

        Reading[] torn = readings.Where(reading => reading.Reads > 1).ToArray();

        Assert.True(
            torn.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{torn.Length} reading(s) of a composite are taken in more than one round trip:"
                + $" {Join(torn.Select(Describe))}. A waiting verb evaluates its predicate against"
                + $" this composite while the surface is still changing, so a re-render landing between"
                + $" two reads composes one object out of two instants — which is not a state the"
                + $" product was ever in. The observed failure was two counts rendered from one record"
                + $" on one element read one after the other: the reminder had arrived by the second"
                + $" read and not by the first, the predicate was satisfied, and the assertion beside"
                + $" it failed against a board that had been right all along. Read the whole composite"
                + $" in a single `EvaluateAsync`, which the browser runs on the thread that applies"
                + $" Blazor's own patches and therefore cannot interleave with one."));
    }

    [Fact]
    public void TheScanReportsATornReadingAndLeavesAWholeOneAndAnUnpolledOneAlone()
    {
        Assert.NotEmpty(TornReadingsIn(TornFixture));
        Assert.Empty(TornReadingsIn(WholeFixture));
        Assert.Empty(TornReadingsIn(UnpolledFixture));
    }

    private sealed record HarnessSource(string Path, string Code);

    private sealed record Reading(string Path, string Method, string Composite, string Body)
    {
        internal int Reads => BrowserReads.Sum(verb => Count(Body, verb));
    }

    private static string Describe(Reading reading) => string.Create(
        CultureInfo.InvariantCulture,
        $"{reading.Path}::{reading.Method} composes {reading.Composite} from {reading.Reads} reads");

    private static Reading[] TornReadingsIn(string code)
    {
        HarnessSource[] one = [new HarnessSource("(fixture)", code)];

        return ReadingsIn(one, PredicateSubjectsIn(one, CompositesIn(one)))
            .Where(reading => reading.Reads > 1)
            .ToArray();
    }

    private static Dictionary<string, bool> CompositesIn(IEnumerable<HarnessSource> harness)
    {
        Dictionary<string, bool> composites = [];

        foreach (HarnessSource source in harness)
        {
            foreach (Match declaration in HarnessRecord.Matches(source.Code))
            {
                composites[declaration.Groups[1].Value] =
                    HasTwoOrMoreParameters(source.Code, declaration.Index + declaration.Length - 1);
            }
        }

        return composites;
    }

    private static HashSet<string> PredicateSubjectsIn(
        IEnumerable<HarnessSource> harness,
        Dictionary<string, bool> composites)
    {
        HashSet<string> subjects = new(StringComparer.Ordinal);

        foreach (HarnessSource source in harness)
        {
            foreach (Match predicate in PredicateParameter.Matches(source.Code))
            {
                string name = predicate.Groups[1].Value;

                if (composites.TryGetValue(name, out bool composite) && composite)
                {
                    subjects.Add(name);
                }
            }
        }

        return subjects;
    }

    private static Reading[] ReadingsIn(
        IEnumerable<HarnessSource> harness,
        HashSet<string> subjects)
    {
        List<Reading> readings = [];

        foreach (HarnessSource source in harness)
        {
            foreach (Match signature in Reader.Matches(source.Code))
            {
                string composite = signature.Groups[1].Value;

                if (!subjects.Contains(composite))
                {
                    continue;
                }

                string body = BodyFollowing(source.Code, signature.Index + signature.Length - 1);

                if (body.Length == 0)
                {
                    continue;
                }

                readings.Add(new Reading(
                    source.Path, signature.Groups[2].Value, composite, body));
            }
        }

        return [.. readings];
    }

    private static bool HasTwoOrMoreParameters(string code, int openParenthesis)
    {
        int depth = 0;

        for (int index = openParenthesis; index < code.Length; index++)
        {
            char current = code[index];

            if (current is '(' or '<' or '[')
            {
                depth++;
                continue;
            }

            if (current is ')' or '>' or ']')
            {
                depth--;

                if (depth == 0)
                {
                    return false;
                }

                continue;
            }

            if (current == ',' && depth == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string BodyFollowing(string code, int openParenthesis)
    {
        int index = AfterMatchingParenthesis(code, openParenthesis);

        if (index < 0)
        {
            return string.Empty;
        }

        while (index < code.Length && char.IsWhiteSpace(code[index]))
        {
            index++;
        }

        if (index < code.Length && code[index] == '{')
        {
            int depth = 0;

            for (int scan = index; scan < code.Length; scan++)
            {
                if (code[scan] == '{')
                {
                    depth++;
                    continue;
                }

                if (code[scan] == '}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        return code[index..(scan + 1)];
                    }
                }
            }

            return string.Empty;
        }

        if (index + 1 < code.Length && code[index] == '=' && code[index + 1] == '>')
        {
            int end = code.IndexOf(';', index);

            return end < 0 ? string.Empty : code[index..end];
        }

        return string.Empty;
    }

    private static int AfterMatchingParenthesis(string code, int openParenthesis)
    {
        int depth = 0;

        for (int index = openParenthesis; index < code.Length; index++)
        {
            if (code[index] == '(')
            {
                depth++;
                continue;
            }

            if (code[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }

        return -1;
    }

    private static int Count(string body, string verb)
    {
        int found = 0;

        for (int index = body.IndexOf(verb, StringComparison.Ordinal);
             index >= 0;
             index = body.IndexOf(verb, index + verb.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static HarnessSource[] ReadHarness()
    {
        List<HarnessSource> harness = [];

        foreach (string path in Directory.EnumerateFiles(
            PathTo(HarnessRelativePath), "*.cs", SearchOption.AllDirectories))
        {
            harness.Add(new HarnessSource(
                Path.GetFileName(path), SourceCode.WithoutComments(File.ReadAllText(path))));
        }

        return [.. harness];
    }

    private static string Join(IEnumerable<string> values)
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
