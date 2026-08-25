using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// No documentation comment in this repository describes a member other than the one it is attached to
/// (TECHNICAL_SPECIFICATION §16.4, §18, <b>F-114</b>).
///
/// <para><b>Why this is a test.</b> A <c>///</c> block binds to the next declaration, and C# has no
/// file-level documentation comment and no diagnostic for a block holding two <c>&lt;summary&gt;</c>
/// elements. So a comment written about one member and left above another compiles, publishes malformed
/// XML documentation, renders whichever summary the tooling picks first, and leaves the member it was
/// written about carrying none. <b>Eight of them were in this tree</b>, in five files, one of them under
/// <c>src/</c>.</para>
///
/// <para><b>What makes it a finding rather than a formatting complaint is the worst of the eight.</b>
/// <c>OrderTestWorld.InsertMenuItemEventSql</c> carried the pre-F-86 account of itself — <em>the payload
/// columns <c>0004</c> and <c>0005</c> added are omitted rather than passed as NULL</em>, and <em>the
/// casts on the two columns that remain</em> — stacked directly above the F-86 correction, which says the
/// statement lists five and names them. F-86 wrote its correction <em>underneath</em> the claim it
/// falsified instead of over it, and because two summaries are legal the claim stayed first. A reader
/// following that comment is told the opposite of what the statement six lines below it does. That is what
/// this mechanism preserves: <b>not an untidy comment, a falsified claim with nothing able to report
/// it</b>.</para>
///
/// <para><b>The other seven are the ordinary shape and are worth naming because each arrived a different
/// way.</b> Slice 59 inserted a method between <c>ReadMenuIndexAsync</c>'s comment and
/// <c>ReadMenuIndexAsync</c>. Two files put a class-level essay at the top, where it bound to whichever
/// record happened to be declared first — so hovering a four-member record in
/// <c>RestaurantInstance.cs</c> produced several hundred words about child processes and WebAuthn origins,
/// and the class itself had no summary at all. <c>CounterJourneys</c> had a forward and an inverse whose
/// comments were swapped, with the inverse's own text saying it was the inverse. And
/// <c>HiddenRecords.razor</c> — a production file — had <c>ListPath</c>'s comment attached to
/// <c>IsExpanded</c>. None of them is a mistake anybody would make deliberately, which is the argument for
/// a rule rather than a habit (F-47).</para>
///
/// <para><b>Why the rule is total rather than a judgement.</b> A documentation comment describes one
/// member and XML documentation gives it one <c>&lt;summary&gt;</c>; every other element it may carry —
/// <c>&lt;param&gt;</c>, <c>&lt;returns&gt;</c>, <c>&lt;para&gt;</c>, <c>&lt;remarks&gt;</c>,
/// <c>&lt;exception&gt;</c> — is repeatable or singular for its own reasons and none of them is affected.
/// So there is no legitimate second summary to exempt, which puts this in the same class as F-81's
/// <em>the token <c>@@section</c> does not appear in this tree</em>: decidable from text with certainty,
/// and therefore a consequence of the language rather than of anybody's preference.</para>
///
/// <para><b>The subject is computed and no file is named</b>, on F-58's lesson: a gate that pinned a
/// filename let its sibling drift four rows away for six slices. Every <c>.cs</c> and <c>.razor</c> file
/// under <c>src/</c> and <c>tests/</c> is walked, every run of consecutive <c>///</c> lines is one block,
/// and a block is reported when it holds more than one opening summary tag. An <em>escaped</em> mention —
/// <c>&amp;lt;summary&amp;gt;</c>, which is how the repairs for this finding refer to the thing they
/// repaired — is not a tag and is not matched, which is F-67's distinction between a use and a mention
/// arriving in a third form.</para>
///
/// <para><b>What it deliberately does not assert.</b> That every member has a summary. That would be a
/// gate about how much documentation this project writes, which is a matter of taste, and it would report
/// findings on hundreds of correct one-line helpers. What is closed is the case that actually happened: a
/// comment whose subject is not the thing underneath it.</para>
/// </summary>
public sealed class DocumentationCommentContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    /// <summary>
    /// Every directory holding authored C# or Razor in this repository (§2). Both, rather than
    /// <c>src/</c> alone: five of F-114's eight sites were under <c>tests/</c>, and the harness is where
    /// this project keeps the reasoning that makes a scenario readable — a comment detached there costs
    /// exactly what one detached in a component costs.
    /// </summary>
    private static readonly string[] SourceRoots = ["src", "tests"];

    /// <summary>The two extensions that carry <c>///</c> blocks in this tree.</summary>
    private static readonly string[] SourceExtensions = ["*.cs", "*.razor"];

    /// <summary>
    /// The opening tag, matched as written rather than by a pattern. An entity-escaped mention is a
    /// different string and is therefore outside this by construction rather than by exclusion.
    /// </summary>
    private const string SummaryOpeningTag = "<summary>";

    /// <summary>
    /// A floor on how much of the tree the walk opened. An emptiness assertion over a walk that read
    /// nothing passes, which is the failure mode of every computed-subject gate in this repository
    /// (F-41) — and this one is more exposed to it than most, because the correct answer is zero.
    /// </summary>
    private const int MinimumFilesScanned = 200;

    /// <summary>
    /// A floor on how many documentation comments were actually parsed. Distinct from the file count and
    /// not redundant with it: a block reader broken in a way that never recognised a block would open
    /// every file, find nothing to report, and satisfy the count above.
    /// </summary>
    private const int MinimumBlocksScanned = 1_500;

    /// <summary>
    /// No documentation comment in the tree holds more than one <c>&lt;summary&gt;</c>.
    /// </summary>
    [Fact]
    public void NoDocumentationCommentDescribesSomethingOtherThanWhatFollowsIt()
    {
        int filesScanned = 0;
        int blocksScanned = 0;
        List<string> problems = [];

        foreach (string path in EnumerateSourceFiles())
        {
            filesScanned++;

            foreach (DocumentationComment block in ReadDocumentationComments(File.ReadAllText(path)))
            {
                blocksScanned++;

                if (block.SummaryCount <= 1)
                {
                    continue;
                }

                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)}:{block.FirstLineNumber} holds {block.SummaryCount}"
                    + $" <summary> elements in one documentation comment"));
            }
        }

        Assert.True(
            filesScanned >= MinimumFilesScanned,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {filesScanned} source file(s) were scanned and this tree has well over"
                + $" {MinimumFilesScanned}, so this fact is not reading the repository it is about — and"
                + $" unlike a list, an empty result is exactly what it expects (F-41)."));

        Assert.True(
            blocksScanned >= MinimumBlocksScanned,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {blocksScanned} documentation comment(s) were parsed out of"
                + $" {filesScanned} file(s), and at least {MinimumBlocksScanned} exist. A block reader"
                + $" that recognised nothing would open every file and report nothing, which is the one"
                + $" way this gate can be green and blind at the same time."));

        Assert.True(
            problems.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{problems.Count} documentation comment(s) hold more than one <summary>:"
                + $" {FormatList(problems)}. A `///` block binds to the next declaration, so the second"
                + $" summary is a comment about something else — the member it was written for has none"
                + $" and this one has two, and the C# compiler says nothing either way (F-114). Move the"
                + $" orphan onto the member it describes; if the two describe the SAME member, one of"
                + $" them is a superseded account of it and is deleted rather than kept, on F-77's"
                + $" ruling — that is how F-86's correction ended up stacked underneath the claim it"
                + $" falsified, where it read as diligence for twenty-two slices."));
    }

    /// <summary>
    /// The scan can see a second summary, tell it from an escaped mention of one, and tell two blocks
    /// apart when a declaration separates them — which is the half an emptiness assertion cannot
    /// demonstrate about itself.
    ///
    /// <para><b>Proven against synthesised source rather than against the tree</b>, and the distinction is
    /// the whole point: the fact above must stay true after the repair, so it cannot depend on the tree
    /// still containing the defect. F-64, F-67 and F-68 each began as an assertion that was true and could
    /// not have detected its own subject.</para>
    ///
    /// <para>Three shapes in both directions. The defect, which is the exact shape all eight sites had.
    /// The escaped mention, which is how every repair for this finding refers to the tag and which a
    /// reader matching the escape would have reported on the very files that record the fix. And two
    /// ordinary adjacent members, which is the shape the walk must not collapse — a reader that ran two
    /// blocks together would report a finding on every well-documented file in the tree.</para>
    ///
    /// <para><b>Every fixture is composed through <see cref="Documented"/> rather than written as a
    /// literal, and that is not tidiness — it is this gate's own subject arriving one register up.</b>
    /// Written out, the defect fixture would put two consecutive <c>///</c> lines carrying a summary each
    /// into <em>this file</em>, and the walk above reads every <c>.cs</c> file under <c>tests/</c> as
    /// text. It cannot tell a comment from a string that looks like one, so the gate reported a finding on
    /// the file proving it works — F-67's distinction between a use and a mention, in a third form, caught
    /// while writing the proof rather than afterwards. The helper keeps the marker out of the file: the
    /// fixtures hold XML fragments and the <c>///</c> is prepended at run time.</para>
    /// </summary>
    [Fact]
    public void TheScanTellsASecondSummaryFromAnEscapedMentionAndFromTheNextBlock()
    {
        // The exact shape all eight F-114 sites had: two summaries, one comment, one member.
        string defect = Documented(
            "<summary>The URL, rebuilt so the record parameter is dropped.</summary>",
            "<summary>Whether this row is the expanded one.</summary>")
            + "    private bool IsExpanded(Row row) => true;\n";

        DocumentationComment[] stacked = [.. ReadDocumentationComments(defect)];

        Assert.Single(stacked.Length);
        Assert.Equal(2, stacked[0].SummaryCount);

        // The escape. Every repair for F-114 writes the tag this way when explaining what it repaired, so
        // a reader that matched it would report a finding on a correct tree.
        string mention = Documented(
            "<summary>",
            "This block was a second &lt;summary&gt; element until F-114, which the compiler",
            "accepts in silence.",
            "</summary>")
            + "    private bool IsExpanded(Row row) => true;\n";

        Assert.Equal(1, Assert.Single(ReadDocumentationComments(mention)).SummaryCount);

        // Two members, two blocks. A declaration between two runs of `///` ends the first one, and a
        // walk that missed that would fail on every documented file in the tree rather than on none.
        string neighbours =
            Documented("<summary>The first.</summary>")
            + "    private int First => 1;\n\n"
            + Documented("<summary>The second.</summary>")
            + "    private int Second => 2;\n";

        DocumentationComment[] separate = [.. ReadDocumentationComments(neighbours)];

        Assert.Equal(2, separate.Length);
        Assert.All(separate, block => Assert.Equal(1, block.SummaryCount));
    }

    /// <summary>
    /// Turns XML fragments into documentation-comment lines, so that a fixture describing a defective
    /// comment does not become one.
    /// </summary>
    private static string Documented(params string[] xml)
        => string.Concat(xml.Select(line => $"    /// {line}\n"));

    /// <summary>
    /// One run of consecutive <c>///</c> lines, which is one documentation comment: the line it starts on,
    /// for the failure message, and how many summaries it opens.
    /// </summary>
    private sealed record DocumentationComment(int FirstLineNumber, int SummaryCount);

    /// <summary>
    /// Every documentation comment in one file, as runs of consecutive <c>///</c> lines.
    ///
    /// <para>Consecutive is the whole of the rule and it is the right one: the C# grammar ends a
    /// documentation comment at the first line that is not one, so anything between two runs — a
    /// declaration, an ordinary <c>//</c> comment, a blank line — makes them two comments about two
    /// things. Leading whitespace is ignored because these are indented to their member.</para>
    /// </summary>
    private static IEnumerable<DocumentationComment> ReadDocumentationComments(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int index = 0;

        while (index < lines.Length)
        {
            if (!IsDocumentationLine(lines[index]))
            {
                index++;
                continue;
            }

            int firstLineNumber = index + 1;
            int summaries = 0;

            while (index < lines.Length && IsDocumentationLine(lines[index]))
            {
                summaries += CountOccurrences(lines[index], SummaryOpeningTag);
                index++;
            }

            yield return new DocumentationComment(firstLineNumber, summaries);
        }
    }

    private static bool IsDocumentationLine(string line)
        => line.TrimStart().StartsWith("///", StringComparison.Ordinal);

    private static int CountOccurrences(string line, string value)
    {
        int found = 0;

        for (int at = line.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = line.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (string root in SourceRoots)
        {
            foreach (string pattern in SourceExtensions)
            {
                foreach (string path in Directory.EnumerateFiles(
                    PathTo(root), pattern, SearchOption.AllDirectories))
                {
                    yield return path;
                }
            }
        }
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
    /// The same walk up to <c>MyRestaurant.slnx</c> every other contract test in this repository uses, and
    /// it throws rather than skips for the same reason: a check that quietly declines to run is worse than
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

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }
}
