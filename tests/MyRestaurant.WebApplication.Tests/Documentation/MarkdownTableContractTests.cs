using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// A table in this repository's documentation is a table (TECHNICAL_SPECIFICATION §16.4, §18).
///
/// <para><b>Why this exists (F-72).</b> The decisions register in §16.4's own specification and the
/// defect ledger in <c>DOCUMENTATION_REVIEW.md</c> had both stopped rendering as tables, in three
/// different ways at once, and every gate in this repository was green throughout. Appendix A's header
/// declared <b>three</b> columns while every row from F-38 onward carried <b>four</b> — and a Markdown
/// renderer truncates a row to its header's width, so the <em>Embodied in</em> cell was discarded on
/// thirty rows. That column is the entire second half of what the register is for: <em>ruling →
/// embodiment</em>. Below it, eight rows — F-63 to F-70, the whole of Slices 33 to 35 — sat after a
/// horizontal rule with no header and no delimiter above them, which makes them literal pipe-delimited
/// text rather than a table at all. And F-65 had no row, because it was fused onto the end of F-64's line
/// by a stray <c>||</c>. In the ledger the same three shapes appeared again: thirty-one rows from F-40 to
/// F-70 broken into fourteen fragments by blank lines, each fragment after the first having lost its
/// header, and one row five cells wide because <c>`ps | grep -m1 postgres`</c> spelled a pipe a table
/// cell reads as a boundary.</para>
///
/// <para><b>What makes it a finding rather than a typo.</b> Nothing here was ever wrong in the source —
/// every character of every row was present and correct, and a reader opening the file in an editor saw
/// all of it. It was wrong only once <em>rendered</em>, which is how the two documents this project runs
/// on are actually read. That is F-49's shape a third time: a thing that existed, worked from one angle,
/// and that nobody had decided. And the damage accumulated in the one direction nothing could catch —
/// each slice appended a row in the shape the previous slice's rows were in, so the drift was invisible
/// precisely <em>because</em> it was consistent.</para>
///
/// <para><b>Why the subject is computed rather than the two registers named.</b> F-58's lesson, and this
/// is the second slice running to apply it: the gate built to stop a version header disagreeing with its
/// own history pinned one filename in a <c>const string</c>, and its sibling document drifted for six
/// slices four rows away. So nothing here names a document. Every Markdown file in the repository is
/// walked, generated text under <c>docs/llm/</c> excluded on the same decision the tree gate excludes it
/// — a context dump is a copy of the authored files, so checking it reports every real finding twice
/// (F-41). The class is named for the property rather than for the register, because naming it
/// <c>DefectRegisterContractTests</c> would be enforcing a general rule against the file that prompted
/// it, which is F-46's lesson and the reason F-63 needed writing at all.</para>
///
/// <para><b>What it deliberately does not assert.</b> That a row's cells say anything in particular; that
/// the registers carry a row for every finding this repository cites (they do not — <b>F-41 is cited
/// fifteen times in <c>DOCUMENTATION_REVIEW.md</c> and has no row of its own there</b>, which is recorded
/// in BUILD_PROGRESS rather than fixed inside a slice about structure); or that a table line closes with
/// its own trailing pipe, which Markdown does not require and a rule about it would be a rule about
/// typography. What is decidable, and what these two facts decide, is whether the text a renderer is
/// handed is a table.</para>
/// </summary>
public sealed class MarkdownTableContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    /// <summary>
    /// Directory names never descended into, spelled the way
    /// <c>ContainerImageReferenceContractTests</c> spells its own list and matched by name at any depth.
    /// <c>llm</c> is generated text, out of scope on the same decision <c>scripts/check_tree.sh</c> makes
    /// about it — a context dump reproduces every authored file, so a finding in one would be reported
    /// twice and a dump's separator structure is not authored Markdown at all (F-41). The rest are build
    /// output and version control, and they are here rather than assumed absent: a package that ships a
    /// <c>README.md</c> lands one under <c>obj/</c> on a restored tree, and a stranger's malformed table
    /// is not this repository's finding.
    /// </summary>
    private static readonly string[] UnreadDirectoryNames =
        [".git", ".vs", "bin", "obj", "llm", "node_modules"];

    /// <summary>The extension this walk reads. Markdown is the only place this tree writes a table.</summary>
    private const string MarkdownPattern = "*.md";

    /// <summary>
    /// A fenced code block's fence, either vocabulary, info string or not.
    ///
    /// <para><b>This is the difference between a gate and a nuisance, and the tree proved it before
    /// anything was planted.</b> `docs/BUILD_PROGRESS.md` quotes the diagnosis `dev_instance.sh` prints
    /// on a failed bring-up, and every line of that quoted output begins with the pipe the helper uses to
    /// indent a container's log. Eighteen such lines sit inside two fences. Read as Markdown they are a
    /// code block; read by a scan that did not know about fences they are two runs of table lines with no
    /// delimiter, which is exactly the finding this file's first fact reports — on a document that is
    /// correct (F-41).</para>
    /// </summary>
    private static readonly Regex CodeFence = new(@"^\s{0,3}(```|~~~)");

    /// <summary>
    /// A table line: a pipe under no more than three spaces of indentation, which is Markdown's own rule.
    /// A fourth space makes an indented code block, and a pipe inside one is text.
    /// </summary>
    private static readonly Regex TableLine = new(@"^\s{0,3}\|");

    /// <summary>
    /// Non-vacuity floors, set well under the census at the time of writing — twenty-four documents,
    /// fifty-five tables, four hundred and seventeen rows. They are floors rather than the numbers
    /// because the numbers are the tree's business and a gate that restated them would be one more count
    /// written in two places, which is the mechanism this file was written after (F-47, F-69).
    /// </summary>
    private const int MinimumDocumentsRead = 12;

    private const int MinimumTablesRead = 30;

    private const int MinimumRowsRead = 200;

    /// <summary>
    /// The delimiter row that turns the line above it into a header: pipes, dashes, colons and space,
    /// and nothing else. It is the one line a renderer requires before it will read anything as a table,
    /// which is why its absence is the first of the two facts below.
    /// </summary>
    private static readonly Regex DelimiterRow = new(@"^\|[\s\-:|]*-[\s\-:|]*\|$");

    /// <summary>
    /// A cell boundary: a pipe that is not escaped. The lookbehind is the whole of what keeps this fact
    /// off a correct tree (F-41) — <c>docs/OPERATIONS.md</c> spells a shell pipeline inside a table cell
    /// as <c>\|</c>, correctly, and a scan that split on every pipe would report that row as three cells
    /// against a two-column header. It was found in the tree rather than planted, which is the better
    /// kind of demonstration.
    /// </summary>
    private static readonly Regex CellBoundary = new(@"(?<!\\)\|");

    /// <summary>
    /// Every run of table lines opens with a header and its delimiter, so a renderer reads it as a table
    /// at all rather than as a paragraph of pipes.
    /// </summary>
    [Fact]
    public void EveryRunOfTableLinesOpensWithAHeaderAndItsDelimiter()
    {
        Census census = ReadDocumentation();

        List<string> problems = [];

        foreach (Run run in census.Runs)
        {
            if (run.Lines.Count < 2)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Where}: one table line on its own ({Excerpt(run.Lines[0])})"));
                continue;
            }

            if (!DelimiterRow.IsMatch(run.Lines[1].Trim()))
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Where}: {run.Lines.Count} table line(s) with no delimiter row beneath the"
                        + $" first ({Excerpt(run.Lines[0])})"));
                continue;
            }

            for (int index = 2; index < run.Lines.Count; index++)
            {
                if (DelimiterRow.IsMatch(run.Lines[index].Trim()))
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{run.Where}: a second delimiter row {index} line(s) in, which starts a table"
                            + " inside a table"));
                }
            }
        }

        AssertTheWalkHappened(census);

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} run(s) of table lines are not tables: {FormatList(problems)}. A renderer"
                + " reads a header only when the line beneath it is a delimiter, so a row separated from"
                + " its header by a blank line — or by a horizontal rule — is not a row in a table, it is"
                + " a paragraph of pipe characters, and the reader is shown exactly that. Delete the"
                + " separator rather than the rows: a register is one table (F-72).");
    }

    /// <summary>
    /// Every row carries the number of cells its own header declares, so no cell is discarded on the way
    /// to the page.
    /// </summary>
    [Fact]
    public void EveryTableRowCarriesTheColumnCountItsHeaderDeclares()
    {
        Census census = ReadDocumentation();

        List<string> problems = [];

        foreach (Run run in census.Runs)
        {
            if (run.Lines.Count < 2 || !DelimiterRow.IsMatch(run.Lines[1].Trim()))
            {
                // Not a table; the fact above this one is the one that says so. Reporting it here as
                // well would state one finding twice and send a reader to the wrong repair.
                continue;
            }

            int declared = CellsIn(run.Lines[0]);

            for (int index = 1; index < run.Lines.Count; index++)
            {
                int held = CellsIn(run.Lines[index]);

                if (held != declared)
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{run.Where}: a row holds {held} cell(s) against a header of {declared}"
                            + $" ({Excerpt(run.Lines[index])})"));
                }
            }
        }

        AssertTheWalkHappened(census);

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} row(s) do not carry their header's column count: {FormatList(problems)}."
                + " A renderer truncates a row to the header's width and discards the rest, silently, so"
                + " a fourth cell under a three-column header is a cell nobody will ever read — which is"
                + " how the last column of a register whose entire subject is *ruling → embodiment* came"
                + " to be dropped from thirty rows (F-72). Either widen the header or escape the pipe:"
                + " a pipe inside a cell is written \\| , as docs/OPERATIONS.md already writes it.");
    }

    /// <summary>
    /// A maximal run of consecutive lines that begin with a pipe — which is the unit a Markdown renderer
    /// decides about. Everything either fact asserts is a property of one of these.
    /// </summary>
    private sealed record Run(string Where, List<string> Lines);

    private sealed record Census(List<Run> Runs, int Documents, int Tables, int Rows);

    /// <summary>
    /// Every Markdown file in the repository, split into runs of table lines. Read once per fact rather
    /// than shared through a fixture, because the cost is a few dozen small files and a shared mutable
    /// census between two facts is a way for one to depend on the other having run.
    /// </summary>
    private static Census ReadDocumentation()
    {
        List<Run> runs = [];
        int documents = 0;
        int tables = 0;
        int rows = 0;

        string root = FindRepositoryRoot().FullName;

        foreach (string path in Directory
            .EnumerateFiles(root, MarkdownPattern, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (IsUnread(path, root))
            {
                continue;
            }

            documents++;

            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            string[] lines = File.ReadAllLines(path);

            List<string> run = [];
            bool fenced = false;

            for (int index = 0; index <= lines.Length; index++)
            {
                if (index < lines.Length && CodeFence.IsMatch(lines[index]))
                {
                    fenced = !fenced;
                }

                bool isTableLine = index < lines.Length && !fenced && TableLine.IsMatch(lines[index]);

                if (isTableLine)
                {
                    run.Add(lines[index]);
                    continue;
                }

                if (run.Count > 0)
                {
                    runs.Add(new Run($"{relative}:{index - run.Count + 1}", run));

                    if (run.Count >= 2 && DelimiterRow.IsMatch(run[1].Trim()))
                    {
                        tables++;
                        rows += run.Count - 2;
                    }

                    run = [];
                }
            }
        }

        return new Census(runs, documents, tables, rows);
    }

    /// <summary>
    /// The guard both facts need and neither can do without: a walk that read nothing reports a clean
    /// repository having opened no file, which is the failure mode of every gate that computes its own
    /// subject (F-41). Three numbers rather than one, because they fail differently — no documents is a
    /// broken walk, no tables is a broken delimiter pattern, and no rows is a pattern that matches a
    /// delimiter and nothing else.
    /// </summary>
    private static void AssertTheWalkHappened(Census census)
    {
        Assert.True(
            census.Documents >= MinimumDocumentsRead,
            $"Only {census.Documents} Markdown file(s) were read and this repository has well over"
                + $" {MinimumDocumentsRead}. The walk is not reading the documentation it is about, and"
                + " both facts here would pass on a correct repository and on a broken one alike (F-41).");

        Assert.True(
            census.Tables >= MinimumTablesRead,
            $"Only {census.Tables} table(s) were recognised across {census.Documents} document(s), and"
                + $" this repository writes well over {MinimumTablesRead}. Either the delimiter pattern"
                + " no longer matches how this tree writes one, or the tables have stopped being"
                + " tables — and the second is the finding, so it must not be reported as a skip.");

        Assert.True(
            census.Rows >= MinimumRowsRead,
            $"Only {census.Rows} row(s) sit inside the {census.Tables} recognised table(s), and this"
                + $" repository writes well over {MinimumRowsRead}. A pattern that matched a delimiter"
                + " row and nothing beneath it would count every table and no content.");
    }

    private static bool IsUnread(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (UnreadDirectoryNames.Contains(segment, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The cells one line declares. Split on unescaped pipes; a line that opens and closes with one
    /// yields an empty string at each end, which is what the two subtracted here are.
    /// </summary>
    private static int CellsIn(string line)
    {
        string[] pieces = CellBoundary.Split(line.Trim());

        return Math.Max(pieces.Length - 2, 0);
    }

    /// <summary>
    /// Enough of a line to recognise it by. Rows in these registers run to three thousand characters, so
    /// a failure message that quoted one would bury the finding it is reporting.
    /// </summary>
    private static string Excerpt(string line)
    {
        string trimmed = line.Trim();

        return trimmed.Length <= 48 ? trimmed : trimmed[..48] + "…";
    }

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);

        return joined.Length == 0 ? "(none)" : joined;
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
