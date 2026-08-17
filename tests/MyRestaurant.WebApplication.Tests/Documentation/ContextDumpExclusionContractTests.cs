using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// What <c>export.sh</c> holds out of the context dump agrees with the tree and with the two shell gates
/// that care about the difference (TECHNICAL_SPECIFICATION §16.4, §18).
///
/// <para><b>Why this exists (F-96).</b> The dump had reached 6.08 MiB, 13% of it a build log whose
/// earliest slices closed months ago, so the log was split and its older half withheld from the dump by
/// path — the way <c>docs/llm/</c> already was. That trade is worth making and it creates a hazard that
/// nothing in the tree could previously see: <b>a session working from <c>dump.txt</c> cannot observe a
/// withheld file at all.</b> It is tracked, it is authored, it is edited by hand, and it is invisible. A
/// document in that state is one careless slice away from being regenerated without it, or from being
/// reconstructed by a session that did not know it was reading half of something.</para>
///
/// <para><b>The rule that makes the trade survivable is fact two, and it is the reason this class is not
/// just bookkeeping.</b> Every withheld document must be linked <em>by path</em> from a document the dump
/// does contain. History that leaves the dump leaves a pointer behind, or it is gone in the only sense
/// that matters — and a pointer is checkable where a habit is not.</para>
///
/// <para><b>Facts three and four are one fact written twice, which is the shape this project has now met
/// eight times.</b> F-48 was a version header against its own changelog; F-50 a variable four documents
/// agreed about and the deployment transport dropped; F-56 a port three helpers dialled and one named
/// correctly; F-65 a touch target stated as a property and rewritten eight pixels short. Here the fact is
/// <em>which held-out directories are generated</em>. <c>scripts/check_tree.sh</c> skips generated trees
/// because a dump's own structure is the separator it forbids (F-40) — and it carried a comment claiming
/// its list was kept in step with the exporter's by hand. After the split that claim is not merely stale
/// but <b>dangerous in one direction</b>: hygiene-exempting the archive because it happens to be excluded
/// from the dump would stop checking 749 KiB of tracked text for exactly the defect F-40 was about. So the
/// generated lists are compared for <em>equality</em> and the archived ones for <em>non-membership</em>,
/// which is the asymmetry the two kinds of exclusion actually have.</para>
///
/// <para><b>Fact four is here because the archive would have failed a gate on arrival.</b>
/// <c>scripts/check_repository.sh</c> forbids a document from asserting platform state (F-42), exempting
/// the files whose job is to <em>quote</em> such a claim. The withheld half of the build log quotes
/// F-42's own sentence. Moving history out of an exempt file into a new file carries the exemption with
/// it or lands red — which was found by running that gate's patterns by hand, and is asserted here so the
/// next archive does not have to rediscover it.</para>
///
/// <para><b>Why the subject is read from the scripts rather than listed here.</b> F-58's lesson, and the
/// same choice <see cref="TestingSectionContractTests"/> makes: a gate that pins the thing it is about in
/// its own <c>const</c> is a gate that keeps passing while its subject moves. Nothing below names a
/// directory. The three arrays are parsed out of <c>export.sh</c>, and if a fourth kind of exclusion is
/// added tomorrow, fact one covers it without an edit here.</para>
///
/// <para><b>What it deliberately does not assert.</b> That the dump is small, or that any particular file
/// is in it. Size is not a property of the tree and a threshold would be a number written twice — the
/// exact defect F-77 rules against. It also does not assert that a session <em>obeyed</em> the pointer,
/// because no artefact this repository produces can see that; the honest residual is that fact two proves
/// the link exists and proves nothing about whether anybody followed it.</para>
/// </summary>
public sealed class ContextDumpExclusionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ExportScriptRelativePath = "export.sh";
    private const string TreeGateRelativePath = "scripts/check_tree.sh";
    private const string RepositoryGateRelativePath = "scripts/check_repository.sh";

    /// <summary>
    /// A Bash array assignment on one line: <c>NAME=("a" "b")</c>. One line rather than a multi-line
    /// form on purpose — the three arrays this reads are declared on one line each precisely so that a
    /// gate can read them without a shell parser, and a future entry that needs wrapping should move the
    /// array into a form both this and a reader can still follow.
    /// </summary>
    private static readonly Regex ArrayAssignment =
        new(@"^(?<name>[A-Z_]+)=\((?<body>[^)]*)\)\s*$", RegexOptions.Multiline);

    /// <summary>A double-quoted element inside such an array.</summary>
    private static readonly Regex QuotedElement = new("\"(?<value>[^\"]*)\"");

    /// <summary>
    /// Every path <c>export.sh</c> holds out of the dump is a real, tracked, non-empty thing.
    ///
    /// <para>Non-vacuity is the point of the count assertion at the end, and it is not decoration (F-41):
    /// a gate that computes its own subject reports nothing at all when the parse silently returns
    /// empty, which is indistinguishable from a tree with nothing wrong.</para>
    /// </summary>
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

    /// <summary>
    /// Every document in a withheld directory is linked by path from a document the dump contains.
    ///
    /// <para>This is the fact that makes withholding history survivable rather than merely cheap. The
    /// link is searched for as a <em>path substring</em> rather than as Markdown, because what matters is
    /// that a reader of the dump can find the file — a bare path in prose does that as well as a link
    /// does, and a gate that demanded link syntax would be a gate about typography (F-70's lesson about
    /// asserting the rule rather than its spelling).</para>
    /// </summary>
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

        // Documents the dump DOES contain: every tracked Markdown file outside every held-out tree.
        // Read as one blob because the question is only whether the path appears anywhere in prose a
        // reader of the dump can reach.
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

    /// <summary>
    /// <c>scripts/check_tree.sh</c> exempts exactly the generated directories from hygiene, and exempts no
    /// archived one. The asymmetry is the whole assertion: tool output cannot be held to a rule about
    /// separators it exists to emit, and authored history can and must be.
    /// </summary>
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

    /// <summary>
    /// <c>scripts/check_repository.sh</c> exempts every archived directory from the platform-state rule,
    /// because an archived build log's job is to quote what this tree used to say — including the claim
    /// that made F-42 possible.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other documentation gates use, failing rather
    /// than skipping for the same reason: a check that quietly declines to run is worse than none.
    /// </summary>
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
