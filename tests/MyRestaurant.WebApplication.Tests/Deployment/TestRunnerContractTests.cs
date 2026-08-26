using System.Text.Json;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class TestRunnerContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string GlobalConfigurationRelativePath = "global.json";
    private const string PackageVersionsRelativePath = "Directory.Packages.props";

    private const string TestingPlatformRunner = "Microsoft.Testing.Platform";

    private static readonly string[] ProhibitedPackages =
    [
        "Microsoft.NET.Test.Sdk",
        "xunit.runner.visualstudio",
    ];

    private const string RequiredPackage = "xunit.v3";

    private static readonly string[] TargetOptions =
    [
        "--solution",
        "--project",
        "--test-modules",
    ];

    private const string ProhibitedOption = "--logger";

    private const int MinimumInvocations = 3;

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

    private static bool ReferencesPackage(string projectText, string package)
        => projectText.Contains($"Include=\"{package}\"", StringComparison.Ordinal)
            || projectText.Contains($"Include='{package}'", StringComparison.Ordinal);

    private static string[] ProjectFiles()
        => Authored(Directory.EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories));

    private static string[] TestProjectFiles()
        => Authored(Directory.EnumerateFiles(RepositoryRoot(), "*.Tests.csproj", SearchOption.AllDirectories));

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
