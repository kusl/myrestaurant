using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

public sealed class MarkdownTableContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private static readonly string[] UnreadDirectoryNames =
        [".git", ".vs", "bin", "obj", "llm", "node_modules"];

    private const string MarkdownPattern = "*.md";

    private static readonly Regex CodeFence = new(@"^\s{0,3}(```|~~~)");

    private static readonly Regex TableLine = new(@"^\s{0,3}\|");

    private const int MinimumDocumentsRead = 12;

    private const int MinimumTablesRead = 30;

    private const int MinimumRowsRead = 200;

    private static readonly Regex DelimiterRow = new(@"^\|[\s\-:|]*-[\s\-:|]*\|$");

    private static readonly Regex CellBoundary = new(@"(?<!\\)\|");

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
                        $"{run.Where}: a second delimiter row {index} line(s) in, which starts a table inside a table"));
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

    [Fact]
    public void EveryTableRowCarriesTheColumnCountItsHeaderDeclares()
    {
        Census census = ReadDocumentation();

        List<string> problems = [];

        foreach (Run run in census.Runs)
        {
            if (run.Lines.Count < 2 || !DelimiterRow.IsMatch(run.Lines[1].Trim()))
            {
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

    private sealed record Run(string Where, List<string> Lines);

    private sealed record Census(List<Run> Runs, int Documents, int Tables, int Rows);

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

    private static int CellsIn(string line)
    {
        string[] pieces = CellBoundary.Split(line.Trim());

        return Math.Max(pieces.Length - 2, 0);
    }

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
