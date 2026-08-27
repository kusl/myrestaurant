using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class ContainerLoggingContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string TestsRelativePath = "tests";

    private const string BuilderConstruction = "new PostgreSqlBuilder(";
    private const string BuilderCompletion = ".Build()";
    private const string LoggerInstallation = ".WithLogger(";

    private const int MinimumFixtures = 2;

    [Fact]
    public void EveryContainerBuilderInstallsItsOwnLoggerBeforeItBuilds()
    {
        IReadOnlyList<string> fixtures = FixtureSources();

        Assert.True(
            fixtures.Count >= MinimumFixtures,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {fixtures.Count} source file(s) under {TestsRelativePath}/ construct a"
                    + $" Testcontainers builder, and this tree has {MinimumFixtures}: the DataAccess"
                    + $" fixture and the end-to-end harness. The walk is not reading the files it is"
                    + $" about, so the fact below would pass on nothing (F-41)."));

        List<string> problems = [];

        foreach (string path in fixtures)
        {
            string source = File.ReadAllText(path);

            for (int constructed = source.IndexOf(BuilderConstruction, StringComparison.Ordinal);
                 constructed >= 0;
                 constructed = source.IndexOf(
                     BuilderConstruction,
                     constructed + BuilderConstruction.Length,
                     StringComparison.Ordinal))
            {
                int completed = source.IndexOf(BuilderCompletion, constructed, StringComparison.Ordinal);

                if (completed < 0)
                {
                    problems.Add(
                        $"{Relative(path)} constructs a builder that no '{BuilderCompletion}' follows, so"
                            + $" this scan cannot decide which logger the container it produces is given");
                    continue;
                }

                if (!source[constructed..completed].Contains(LoggerInstallation, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{Relative(path)} constructs a builder and reaches '{BuilderCompletion}' without"
                            + $" calling '{LoggerInstallation}'");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{problems.Count} container builder(s) here do not choose their own logger:"
                    + $" {Format(problems)}. Testcontainers seeds every builder with its console logger"
                    + $" and then reports each container created, each readiness probe and each container"
                    + $" deleted at Information through it — several hundred lines per run of this suite,"
                    + $" with the one assertion that failed somewhere inside. The logger is a value on"
                    + $" the builder's own resource configuration rather than a global setting, so it has"
                    + $" to be installed on the chain that reaches {BuilderCompletion} and installing it"
                    + $" anywhere else silences nothing (F-126). What diagnoses a container that will not"
                    + $" start here is the fixture's own DescribeFailure prose, not those lines"
                    + $" (F-125)."));
    }

    private static IReadOnlyList<string> FixtureSources()
    {
        string ownFileName = nameof(ContainerLoggingContractTests) + ".cs";

        return Directory
            .EnumerateFiles(PathTo(TestsRelativePath), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderGeneratedDirectory(path))
            .Where(path => !string.Equals(Path.GetFileName(path), ownFileName, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(BuilderConstruction, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsUnderGeneratedDirectory(string path)
    {
        foreach (string segment in path.Split(Path.DirectorySeparatorChar))
        {
            if (string.Equals(segment, "bin", StringComparison.Ordinal)
                || string.Equals(segment, "obj", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Relative(string path)
        => Path
            .GetRelativePath(FindRepositoryRoot().FullName, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string Format(IEnumerable<string> values)
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
