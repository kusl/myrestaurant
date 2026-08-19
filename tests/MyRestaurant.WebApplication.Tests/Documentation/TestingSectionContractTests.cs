using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// §16.4's account of a contract test agrees with the contract test (TECHNICAL_SPECIFICATION §16.4, §18).
///
/// <para><b>Why this exists (F-70).</b> An eighth assertion appeared in
/// <c>HandheldLayoutContractTests</c> without a row in the defect ledger, a paragraph in §16.4, a
/// changelog entry or a line in <c>_CHANGES.md</c> — which is every artefact this project's
/// atomic-documentation rule requires of a behaviour change (R§10, S§18). The rule it asserts is a good
/// one and it was kept. What went missing was the paperwork, and what let it stay missing is the
/// interesting part: <b>§16.4 said seven and the file held eight, and nothing in this tree compared the
/// two.</b></para>
///
/// <para><b>The number was written twice, which is the mechanism this project has now met six times.</b>
/// F-48 was a version header against its own changelog. F-50 was a variable four documents agreed about
/// and the deployment transport dropped. F-56 was a port three helpers dialled and one named correctly.
/// F-65 was a touch target stated as a property and re-written as a literal eight pixels short. Each is
/// one fact recorded in two places, and in each the two places disagreed. A count of counted classes in
/// prose is exactly that shape, and <b>this class carried three copies of it and two went stale
/// (F-89)</b>. F-73 found the first — the summary here said eight when §16.4 held nine, wrong on arrival,
/// by one, inside the class whose subject is that kind of wrongness — and ruled that the number should be
/// <em>kept</em> because it was the argument for the floor below, with the habit of moving it added
/// beside it. <b>The habit did not hold.</b> Across the two census moves that followed, this summary and
/// §16.4's own paragraph were each left where they were: the summary said sixteen in a sentence whose
/// next clause said eighteen, and §16.4 said ten through both moves, stale by nine. Three slices of
/// evidence now say a census cannot be maintained by hand in three places, so F-77's ruling wins over
/// F-73's: both prose copies are <b>deleted</b>, and <see cref="MinimumCountedClasses"/> is the only one
/// left. That copy is safe in the way the others were not — it is enforced on every run, so it cannot go
/// stale silently, only loudly.</para>
///
/// <para><b>It has now caught the thing it was written for, and the catch is worth reading (F-82).</b>
/// Slice 40 added assertions to four classes §16.4 cites — 23 → 26, 5 → 7, 11 → 13, 5 → 7 — and moved
/// none of the four numbers. This file would have said so on the next run. There was no next run: the
/// same slice left two Razor components naming a loop variable after a reserved directive, so
/// <c>MyRestaurant.WebApplication</c> did not compile, so the test project that depends on it did not
/// execute (F-81). <b>A gate that cannot run is indistinguishable from a gate that passed</b>, which is
/// F-71's lesson arriving from the other direction — that one was a test project that failed to compile
/// behind a green-looking summary line, this one is a gate that never started behind a build error
/// everybody was already looking at.</para>
///
/// <para><b>And there was a second copy of the same fact that WAS read, and it was not chased.</b> This
/// project predicts its own test count as arithmetic every slice — Slice 34 predicted 1077 — and the run
/// returned 1078. One unexplained test is one undocumented gate, and the difference sat in a terminal log
/// nobody reconciled. That is not something a test can fix; it is a habit, and it is written into §18
/// beside this gate rather than left as folklore.</para>
///
/// <para><b>Why the subject is computed rather than listed.</b> F-58's lesson, applied on the first
/// opportunity: the gate built to stop a version header disagreeing with its own history pinned one
/// filename in a <c>const string</c>, and the sibling document drifted for six slices four rows away. So
/// nothing here names a test class. §16.4 is read, every backticked <c>*Tests.cs</c> it cites is resolved
/// against the tree by filename, and where the same paragraph states a count, the two are compared. Both
/// citation forms are admitted — the full repo-relative path and the elided
/// <c>…/Documentation/SpecificationVersionTests.cs</c> — because the document uses both and a gate that
/// only understood one would be a gate about typography.</para>
///
/// <para><b>What it deliberately does not assert, and the residual is real.</b> That §16.4 mentions every
/// test class in the tree. It does not and should not: the three response-header classes are described as
/// a group, by directory, and that is better prose for the reader §16.4 exists for. So a brand-new test
/// file that the document never cites at all remains invisible to this gate — which is a weaker version
/// of the hole F-70 fell through, and it is recorded rather than papered over. What is closed is the case
/// that actually happened: a cited class gaining an assertion the citation does not know about. Removing a
/// count to dodge the comparison is caught by <see cref="MinimumCountedClasses"/>, since a paragraph that
/// stops stating one stops being a pair.</para>
/// </summary>
public sealed class TestingSectionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";
    private const string TestsRelativePath = "tests";

    /// <summary>Where §16.4 begins, as it is written in the document.</summary>
    private const string SectionOpening = "**16.4 CI:**";

    /// <summary>
    /// How many test classes §16.4 must turn out to state a count for. Without a floor, a document that
    /// had stopped stating counts — or a section marker that had been reworded, so the slice read is
    /// empty — would satisfy every comparison below by having none to make, which is the failure mode of
    /// every gate that computes its own subject (F-41).
    ///
    /// <para>This is the <em>only</em> place the census is written down (F-89). It moves with §16.4 —
    /// twelve, sixteen, eighteen, nineteen as the vocabulary gate joined the section, twenty with the
    /// identifier ordering gate, twenty-one with the context-dump exclusion gate, twenty-three as the
    /// test-runner gate and the resequencing verb's integration facts joined it together, twenty-four with
    /// the item resequencing facts, twenty-five with the menu grouping gate, and now twenty-seven as the
    /// menu image schema brings a pure-function class and an integration class together — and it is the one
    /// copy that cannot drift unnoticed, because a census that fell below it fails here rather than sitting
    /// in a sentence nothing reads.</para>
    ///
    /// <para>A floor rather than an equality, on purpose. An equality would turn every paragraph that
    /// merely <em>describes</em> a class without enumerating it into a failure, and §16.4 is prose — the
    /// three response-header classes are legitimately covered as a group by directory. What the floor
    /// refuses is the collapse to zero, which is the one failure a computed subject cannot report on
    /// itself.</para>
    /// </summary>
    private const int MinimumCountedClasses = 28;

    /// <summary>
    /// A test class named inside backticks, in either of the two forms §16.4 uses: the full
    /// repo-relative path, and the elided form that begins with a horizontal ellipsis. Only the file
    /// name is captured, because that is what both forms have in common and what the tree can be
    /// searched by.
    /// </summary>
    private static readonly Regex CitedTestClass = new(@"`[^`]*?([A-Za-z0-9_]+Tests\.cs)`");

    /// <summary>
    /// A count of assertions as §16.4 writes it: a word or a number, then the noun. The word is
    /// validated against <see cref="NumberWords"/> rather than by the pattern, so ordinary prose —
    /// <em>"the second assertion below"</em>, <em>"that assertion"</em> — is read and discarded rather
    /// than mistaken for a claim.
    /// </summary>
    private static readonly Regex AssertionCount = new(@"\b([A-Za-z]+|[0-9]+)\s+assertions?\b");

    /// <summary>
    /// The numbers §16.4 spells out. Deliberately short: a contract test with more than twelve
    /// assertions is a contract test that has become two, and the gate refusing to parse the word is a
    /// reasonable place to find that out.
    /// </summary>
    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six",
        "seven", "eight", "nine", "ten", "eleven", "twelve",
    ];

    /// <summary>An xUnit test method's attribute, which is the unit §16.4 counts.</summary>
    private static readonly Regex TestAttribute = new(@"\[(Fact|Theory)\b");

    /// <summary>
    /// Every claim §16.4 makes about how many assertions a contract test holds is true of the file.
    /// </summary>
    [Fact]
    public void SectionSixteenFourCountsWhatTheContractTestsHold()
    {
        string section = ReadTestingSection();

        Dictionary<string, string> testFilesByName = TestFilesByName();

        List<string> ambiguous = [];
        List<string> uncited = [];
        List<string> disagreements = [];
        List<string> counted = [];

        // Paragraphs, split on the blank line between them. That is a safe unit in this tree rather
        // than an assumption about Markdown: the tree gate asserts that no line is whitespace-only, so
        // a blank line is genuinely empty and a paragraph is genuinely one run of prose.
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
                // Described without a count. Not a finding: §16.4 is prose and a paragraph may explain
                // what a class asserts without enumerating it. Nothing to compare, so nothing is said.
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

        // Non-vacuity, and here it is also the anti-evasion guard. Deleting the number from a paragraph
        // makes it stop being a pair, so without a floor the cheapest way to satisfy this test would be
        // to stop making the claim — which is the documentation getting worse to keep a gate green.
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

    /// <summary>
    /// §16.4, from its opening marker to the start of the next numbered section. Read as a slice of the
    /// specification rather than from a heading level, because §16.4 is a bold run-in paragraph rather
    /// than a Markdown heading — and a gate that assumed otherwise would read nothing and say nothing.
    /// </summary>
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

    /// <summary>
    /// Every test class in the tree, by file name. A name that occurs twice is not indexed at all, so a
    /// citation that could mean either file is reported as uncited rather than resolved to a guess.
    /// </summary>
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

    /// <summary>
    /// A spelled or written number, or <c>-1</c> for a word that is not one. Ordinary prose reaches this
    /// method constantly — <em>"that assertion"</em>, <em>"every assertion"</em> — and returning a
    /// sentinel rather than throwing is what keeps those out of the count without a second pattern.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other documentation and contract tests use, and
    /// it fails rather than skips for the same reason: a check that quietly declines to run is worse than
    /// none.
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
