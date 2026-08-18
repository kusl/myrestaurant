using System.Text.Json;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

/// <summary>
/// One test runner, selected once, and every invocation of <c>dotnet test</c> in the tree spelled for it
/// (TECHNICAL_SPECIFICATION §16, §16.4, <b>F-97</b>).
///
/// <para><b>Why this exists.</b> <c>xunit.v3</c> 4.0.0 installs <c>xunit.v3.mtp-v2</c>, which pins
/// Microsoft.Testing.Platform 2, and MTP 2 removed the VSTest target for the .NET 10 SDK. So a tree that
/// bumps the package and keeps <c>Microsoft.NET.Test.Sdk</c> and <c>xunit.runner.visualstudio</c> does not
/// build at all — it reports <em>Testing with VSTest target is no longer supported…</em> from an MSBuild
/// targets file inside the NuGet cache, four times, with the project names and nothing about the packages
/// that caused it. That failure is loud, which is the good case. <b>The quiet one is what this gate is
/// for:</b> the two mechanisms are selected in four different places — a stanza in <c>global.json</c>, a
/// package reference per test project, a version pin in <c>Directory.Packages.props</c>, and the command
/// line in every script and workflow — and each of those can be moved on its own. A tree half-migrated
/// between runners is a tree where <c>dotnet test</c> means something different depending on which file
/// somebody last edited.</para>
///
/// <para><b>The subject is computed rather than listed</b> (F-47's habit, F-58's lesson). Nothing here
/// names a test project: every <c>*.Tests.csproj</c> under the tree is found and required to be an
/// xUnit.net v3 application, so a fifth test project cannot arrive on a different runner while these four
/// stay right. The same holds for the command lines — every tracked script and workflow is read, rather
/// than the two files that happen to hold an invocation today.</para>
///
/// <para><b>What it deliberately does not assert.</b> That <c>dotnet test</c> was ever run, that MTP was
/// the runner that answered, or that the SDK on the machine is one that has the mode at all — those are
/// properties of a host, and a gate that guessed at them from a version string would report findings on
/// correct trees (F-41). What is closed is the case that happened: one half of a runner migration landing
/// without the other three.</para>
///
/// <para><b>Non-vacuity comes first in each fact, and here it is also the anti-evasion guard.</b> Every
/// scan below asserts it found its subject before it asserts anything about it: a rename that made this
/// class unable to find the projects, the scripts or the workflows fails on that rather than passing with
/// nothing compared, and deleting the last invocation of <c>dotnet test</c> from the repository fails the
/// same way.</para>
/// </summary>
public sealed class TestRunnerContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string GlobalConfigurationRelativePath = "global.json";
    private const string PackageVersionsRelativePath = "Directory.Packages.props";

    /// <summary>
    /// The runner name the .NET 10 SDK reads out of <c>global.json</c>'s <c>test</c> section. Written as
    /// the literal string rather than assembled, because it is a value the SDK compares and not a label.
    /// </summary>
    private const string TestingPlatformRunner = "Microsoft.Testing.Platform";

    /// <summary>
    /// The VSTest half of a test project. Both must be absent from every project file and from the central
    /// version list — <em>absent</em> rather than merely unreferenced, because a version pin standing ready
    /// for a package that cannot be used is an invitation with a comment on it.
    /// </summary>
    private static readonly string[] ProhibitedPackages =
    [
        "Microsoft.NET.Test.Sdk",
        "xunit.runner.visualstudio",
    ];

    /// <summary>The one test framework package this tree takes, versionless (central package management).</summary>
    private const string RequiredPackage = "xunit.v3";

    /// <summary>
    /// How the MTP mode of <c>dotnet test</c> is given something to run. A path that is not introduced by
    /// one of these is a VSTest-mode command line: in MTP mode a bare argument is read as a directory to
    /// search, so <c>dotnet test MyRestaurant.slnx</c> does not fail with a message about the runner — it
    /// looks for a folder of that name, which is the shape of failure this list exists to prevent.
    /// </summary>
    private static readonly string[] TargetOptions =
    [
        "--solution",
        "--project",
        "--test-modules",
    ];

    /// <summary>
    /// The VSTest option that has no MTP equivalent and is silently ignored where it is accepted at all.
    /// <c>--logger "trx"</c> is what CI passed for eight milestones; the report is asked for from the test
    /// application now, after the <c>--</c>.
    /// </summary>
    private const string ProhibitedOption = "--logger";

    /// <summary>
    /// At least this many invocations of <c>dotnet test</c> must be found. Four exist — two in
    /// <c>.github/workflows/ci.yml</c>, two in <c>scripts/ci_local.sh</c> — and the floor is deliberately
    /// below that, so a slice that moves one gate around does not fail here for a reason that is not a
    /// finding, while a tree that had lost every invocation still cannot satisfy the assertion by having
    /// nothing to check (F-41).
    /// </summary>
    private const int MinimumInvocations = 3;

    /// <summary>
    /// The runner is selected once, for the repository, in the file the SDK reads before anything else.
    /// </summary>
    [Fact]
    public void TheRepositorySelectsTheTestingPlatformRunnerInGlobalJson()
    {
        string text = ReadFromRepository(GlobalConfigurationRelativePath);

        using JsonDocument document = JsonDocument.Parse(text);

        Assert.True(
            document.RootElement.TryGetProperty("sdk", out JsonElement sdk)
                && sdk.TryGetProperty("version", out JsonElement _),
            $"{GlobalConfigurationRelativePath} has no sdk.version, so this test is not reading the file"
                + " §16 describes and the assertion below would be about the wrong document (F-41).");

        Assert.True(
            document.RootElement.TryGetProperty("test", out JsonElement test),
            $"{GlobalConfigurationRelativePath} has no 'test' section. That stanza is the .NET 10 opt-in"
                + $" into the {TestingPlatformRunner} mode of `dotnet test`; without it the SDK runs the"
                + " VSTest mode, which MTP 2 refuses on this SDK, and the whole suite fails to build.");

        Assert.True(
            test.TryGetProperty("runner", out JsonElement runner),
            $"{GlobalConfigurationRelativePath}'s 'test' section names no runner.");

        Assert.Equal(TestingPlatformRunner, runner.GetString());
    }

    /// <summary>
    /// No project carries a VSTest adapter, and no version is pinned for one.
    /// </summary>
    [Fact]
    public void NothingInTheTreeReferencesOrPinsAVSTestAdapter()
    {
        string[] projects = ProjectFiles();

        Assert.True(
            projects.Length >= 4,
            $"Only {projects.Length} project file(s) were found and this tree has seven. The walk is not"
                + " reading the repository it is about, so the assertion below would pass on nothing"
                + " (F-41).");

        List<string> findings = [];

        foreach (string project in projects)
        {
            string text = File.ReadAllText(project);

            foreach (string package in ProhibitedPackages)
            {
                if (ReferencesPackage(text, package))
                {
                    findings.Add($"{Relative(project)} references {package}");
                }
            }
        }

        string versions = ReadFromRepository(PackageVersionsRelativePath);

        foreach (string package in ProhibitedPackages)
        {
            if (ReferencesPackage(versions, package))
            {
                findings.Add($"{PackageVersionsRelativePath} pins {package}");
            }
        }

        Assert.True(
            findings.Count == 0,
            $"The VSTest adapter is prohibited in this tree (§16, F-97) and {findings.Count} reference(s)"
                + $" remain: {FormatList(findings)}. `xunit.v3` carries Microsoft.Testing.Platform support"
                + " natively; the adapter packages are what MTP 2 refuses on the .NET 10 SDK.");
    }

    /// <summary>
    /// Every test project is an xUnit.net v3 application: the framework package, and the executable shape
    /// that comes with it.
    /// </summary>
    [Fact]
    public void EveryTestProjectIsAnXunitApplication()
    {
        string[] projects = TestProjectFiles();

        Assert.True(
            projects.Length >= 4,
            $"Only {projects.Length} test project(s) were found and §2 describes four. Either the walk is"
                + " wrong or a project was renamed out of the *.Tests.csproj shape this scan reads (F-41).");

        List<string> findings = [];

        foreach (string project in projects)
        {
            string text = File.ReadAllText(project);

            if (!ReferencesPackage(text, RequiredPackage))
            {
                findings.Add($"{Relative(project)} does not reference {RequiredPackage}");
            }

            if (!text.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal))
            {
                findings.Add($"{Relative(project)} is not OutputType=Exe");
            }
        }

        Assert.True(
            findings.Count == 0,
            $"{findings.Count} test project(s) are not the shape §16 describes: {FormatList(findings)}."
                + " An xUnit.net v3 test project is a console application that hosts its own runner, and"
                + " one that is not is a project `dotnet test` cannot run in MTP mode.");
    }

    /// <summary>
    /// Every invocation of <c>dotnet test</c> in a script or a workflow is spelled for the runner the
    /// repository selected.
    /// </summary>
    [Fact]
    public void EveryInvocationIsSpelledForTheTestingPlatformMode()
    {
        List<string> invocations = [];
        List<string> findings = [];

        foreach (string path in ScriptAndWorkflowFiles())
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();

                // A comment may quote a command line — the two files this gate is about both explain the
                // migration in comments that name the old spelling on purpose — so what a line is comes
                // before what it says.
                if (trimmed.StartsWith('#'))
                {
                    continue;
                }

                if (trimmed.Contains(ProhibitedOption, StringComparison.Ordinal))
                {
                    findings.Add(
                        $"{Relative(path)} passes {ProhibitedOption}, which VSTest read and MTP does not");
                }

                int start = trimmed.IndexOf("dotnet test", StringComparison.Ordinal);

                if (start < 0)
                {
                    continue;
                }

                invocations.Add(Relative(path));

                string[] rest = trimmed[(start + "dotnet test".Length)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Nothing after the verb, or a line continuation: a bare `dotnet test` searches the
                // current directory, which is exactly what the README documents.
                if (rest.Length == 0 || rest[0] == "\\")
                {
                    continue;
                }

                if (!rest[0].StartsWith('-'))
                {
                    findings.Add(
                        $"{Relative(path)} passes '{rest[0]}' as a bare argument; in MTP mode a target"
                            + $" arrives through one of {string.Join(", ", TargetOptions)}");
                }
            }
        }

        Assert.True(
            invocations.Count >= MinimumInvocations,
            $"Only {invocations.Count} invocation(s) of `dotnet test` were found in the tree's scripts and"
                + $" workflows, and at least {MinimumInvocations} are expected. Either the scan is not"
                + " reading them or the test gates have left CI, and both make every check above vacuous"
                + " (F-41).");

        Assert.True(
            findings.Count == 0,
            $"{findings.Count} invocation(s) are written for VSTest rather than for the runner"
                + $" {GlobalConfigurationRelativePath} selects: {FormatList(findings)}. In MTP mode a"
                + " solution or project is named with an option, `--logger` does not exist, and arguments"
                + " for the test application itself go after a `--`.");
    }

    /// <summary>
    /// Whether an MSBuild file <em>declares</em> a package rather than merely naming it, and the
    /// distinction is the whole of what keeps this gate off correct trees (F-41, and F-67's standard —
    /// <em>declared, not merely mentioned</em>). Every one of the four test projects explains in a comment
    /// which packages left it and why, so a scan for the bare name reports two findings on the tree that
    /// is right. It is looked for as an <c>Include</c> attribute value; both quote forms are admitted
    /// because MSBuild accepts both, and a gate that understood one would be a gate about typography.
    ///
    /// <para><b>Proven not to fire</b> on a comment naming both prohibited packages, which
    /// <c>MyRestaurant.Domain.Tests.csproj</c> deliberately contains — so a future version of this helper
    /// that went back to a substring search fails on arrival rather than quietly bounding its own reach.</para>
    /// </summary>
    private static bool ReferencesPackage(string projectText, string package)
        => projectText.Contains($"Include=\"{package}\"", StringComparison.Ordinal)
            || projectText.Contains($"Include='{package}'", StringComparison.Ordinal);

    /// <summary>
    /// Every project file in the tree, build output excluded. <c>bin</c> and <c>obj</c> hold generated
    /// props and, under the end-to-end project, a shipped shell script — neither is authored text, and a
    /// gate that read them would report findings that depend on whether somebody had built recently.
    /// </summary>
    private static string[] ProjectFiles()
        => Authored(Directory.EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories));

    private static string[] TestProjectFiles()
        => Authored(Directory.EnumerateFiles(RepositoryRoot(), "*.Tests.csproj", SearchOption.AllDirectories));

    /// <summary>
    /// Every shell script and every workflow, which between them hold every invocation of
    /// <c>dotnet test</c> this repository makes. <c>README.md</c> is deliberately not read: it documents
    /// the commands for a person, in prose, and a gate over prose is a gate about typography.
    /// </summary>
    private static string[] ScriptAndWorkflowFiles()
    {
        string root = RepositoryRoot();

        List<string> paths = [];
        paths.AddRange(Authored(Directory.EnumerateFiles(root, "*.sh", SearchOption.AllDirectories)));
        paths.AddRange(Authored(Directory.EnumerateFiles(
            Path.Combine(root, ".github", "workflows"), "*.yml", SearchOption.AllDirectories)));

        return [.. paths];
    }

    private static string[] Authored(IEnumerable<string> paths)
        => [.. paths
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)];

    private static string Relative(string path)
        => Path.GetRelativePath(RepositoryRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static string ReadFromRepository(string relativePath)
    {
        string path = Path.Combine(
            RepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                    + " §2 describes.");
        }

        return File.ReadAllText(path);
    }

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other contract tests use, and it fails rather
    /// than skips for the same reason: a check that quietly declines to run is worse than none.
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
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }
}
