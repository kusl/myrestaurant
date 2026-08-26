using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

public sealed class ContextDumpExclusionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ExportScriptRelativePath = "export.sh";
    private const string TreeGateRelativePath = "scripts/check_tree.sh";
    private const string RepositoryGateRelativePath = "scripts/check_repository.sh";

    private static readonly Regex ArrayAssignment =
        new(@"^(?<name>[A-Z_]+)=\((?<body>[^)]*)\)\s*$", RegexOptions.Multiline);

    private static readonly Regex QuotedElement = new("\"(?<value>[^\"]*)\"");

    [Fact]
    public void EveryHeldOutPathExistsAndHoldsSomething()
    {
        string script = ReadRepositoryFile(ExportScriptRelativePath);

        string[] directories =
        [
            .. ArrayElements(script, "GENERATED_DIRECTORIES"),
            .. ArrayElements(script, "ARCHIVED_DIRECTORIES"),
        ];

        string[] files = ArrayElements(script, "ELIDED_FILES");

        List<string> problems = [];

        foreach (string directory in directories)
        {
            string absolute = PathTo(directory);

            if (!Directory.Exists(absolute))
            {
                problems.Add($"'{directory}' is held out of the dump and is not a directory in this tree");
                continue;
            }

            if (!Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories).Any())
            {
                problems.Add($"'{directory}' is held out of the dump and is empty, so the exclusion is stale prose");
            }
        }

        foreach (string file in files)
        {
            if (!File.Exists(PathTo(file)))
            {
                problems.Add($"'{file}' is elided from the dump and is not a file in this tree");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"export.sh holds out a path the tree does not have: {Format(problems)}. An exclusion that"
                + " resolves to nothing is worse than none: the dump's own header announces it to a reader"
                + " as something withheld, so a reader concludes there is history to go and find.");

        Assert.True(
            directories.Length + files.Length >= 3,
            $"Only {directories.Length + files.Length} held-out path(s) were parsed out of"
                + $" {ExportScriptRelativePath}, and there are at least three — docs/llm, docs/progress and"
                + " LICENSE. The array parse is not reading the script it is about, so every check above"
                + " passed by having nothing to look at (F-41).");
    }

    [Fact]
    public void EveryWithheldDocumentIsLinkedFromADumpedDocument()
    {
        string script = ReadRepositoryFile(ExportScriptRelativePath);
        string[] archived = ArrayElements(script, "ARCHIVED_DIRECTORIES");
        string[] generated = ArrayElements(script, "GENERATED_DIRECTORIES");

        Assert.True(
            archived.Length > 0,
            $"No ARCHIVED_DIRECTORIES were parsed out of {ExportScriptRelativePath}. If the archive has"
                + " genuinely been retired, delete this test with it; while the array exists this parse"
                + " returning nothing means the check below is vacuous.");

        string[] withheldPrefixes = [.. archived, .. generated];

        string dumped = string.Concat(
            Directory.EnumerateFiles(RepositoryRoot(), "*.md", SearchOption.AllDirectories)
                .Select(Relative)
                .Where(path => !withheldPrefixes.Any(
                    prefix => path.StartsWith(prefix + "/", StringComparison.Ordinal)))
                .Select(path => File.ReadAllText(PathTo(path))));

        List<string> orphans = [];

        foreach (string directory in archived)
        {
            foreach (string document in Directory.EnumerateFiles(
                PathTo(directory), "*.md", SearchOption.AllDirectories))
            {
                string path = Relative(document);

                if (!dumped.Contains(path, StringComparison.Ordinal))
                {
                    orphans.Add(path);
                }
            }
        }

        Assert.True(
            orphans.Count == 0,
            $"{orphans.Count} withheld document(s) are named by nothing the dump contains:"
                + $" {Format(orphans)}. A session reading dump.txt cannot see these files and has no way"
                + " to learn they exist, so the next slice regenerates the document they were split out"
                + " of without them. History that leaves the dump leaves a pointer behind.");
    }

    [Fact]
    public void TreeHygieneSkipsGeneratedTreesAndChecksArchivedOnes()
    {
        string exporter = ReadRepositoryFile(ExportScriptRelativePath);
        string treeGate = ReadRepositoryFile(TreeGateRelativePath);

        string[] exporterGenerated = ArrayElements(exporter, "GENERATED_DIRECTORIES");
        string[] gateGenerated = ArrayElements(treeGate, "GENERATED_DIRECTORIES");
        string[] archived = ArrayElements(exporter, "ARCHIVED_DIRECTORIES");

        Assert.True(
            exporterGenerated.Length > 0 && gateGenerated.Length > 0,
            "One of the two GENERATED_DIRECTORIES arrays parsed as empty, so the comparison below is"
                + $" between two empty sets: {ExportScriptRelativePath} gave {exporterGenerated.Length},"
                + $" {TreeGateRelativePath} gave {gateGenerated.Length}.");

        Assert.True(
            exporterGenerated.OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(gateGenerated.OrderBy(name => name, StringComparer.Ordinal),
                    StringComparer.Ordinal),
            $"{ExportScriptRelativePath} calls {Format(exporterGenerated)} generated and"
                + $" {TreeGateRelativePath} calls {Format(gateGenerated)} generated. One fact, two files,"
                + " and they have drifted — which is F-50's shape. A tree the exporter treats as tool"
                + " output and the hygiene gate treats as authored will fail on a dump's own separators;"
                + " the reverse leaves real files unchecked.");

        List<string> exempted = [.. archived.Where(
            directory => gateGenerated.Contains(directory, StringComparer.Ordinal))];

        Assert.True(
            exempted.Count == 0,
            $"{Format(exempted)} is withheld from the dump AND exempt from tree hygiene. Those are"
                + " different reasons and only one of them is a property of the file: an archived"
                + " directory holds hand-written text, and text nothing checks is where an appended"
                + " separator line lived undetected across twenty-one files (F-40).");
    }

    [Fact]
    public void ThePlatformStateRuleExemptsArchivedHistory()
    {
        string exporter = ReadRepositoryFile(ExportScriptRelativePath);
        string repositoryGate = ReadRepositoryFile(RepositoryGateRelativePath);

        string[] archived = ArrayElements(exporter, "ARCHIVED_DIRECTORIES");
        string[] records = ArrayElements(repositoryGate, "RECORD_FILES");

        Assert.True(
            records.Length > 0,
            $"No RECORD_FILES were parsed out of {RepositoryGateRelativePath}. That array is how the"
                + " platform-state rule knows which files are allowed to quote a platform claim, and an"
                + " empty parse makes the check below pass by looking at nothing (F-41).");

        List<string> unexempted = [.. archived.Where(
            directory => !records.Contains(directory + "/*", StringComparer.Ordinal)
                         && !records.Contains(directory, StringComparer.Ordinal))];

        Assert.True(
            unexempted.Count == 0,
            $"{Format(unexempted)} holds archived history and is not a RECORD_FILE in"
                + $" {RepositoryGateRelativePath}. A build log quotes the sentences this project got"
                + " wrong — that is what a log is for — and gate 3 reads such a quotation as the offence"
                + " itself. Moving history into a new file carries the exemption with it, or the first"
                + " run after the move is red for a reason that has nothing to do with the move.");
    }

    private static string[] ArrayElements(string script, string name)
    {
        foreach (Match match in ArrayAssignment.Matches(script))
        {
            if (!string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
            {
                continue;
            }

            return [.. QuotedElement.Matches(match.Groups["body"].Value)
                .Select(element => element.Groups["value"].Value)
                .Where(value => value.Length > 0 && !value.StartsWith('$'))];
        }

        return [];
    }

    private static string Format(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }

    private static string ReadRepositoryFile(string relativePath)
        => File.ReadAllText(PathTo(relativePath));

    private static string Relative(string absolutePath)
        => Path.GetRelativePath(RepositoryRoot(), absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static string PathTo(string relativePath)
    {
        string path = Path.Combine(
            RepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                    + " §2 describes.");
        }

        return path;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate.FullName;
            }
        }

        throw new InvalidOperationException(
            $"'{SolutionFileName}' was not found above '{AppContext.BaseDirectory}', so the repository"
                + " root cannot be located.");
    }
}
