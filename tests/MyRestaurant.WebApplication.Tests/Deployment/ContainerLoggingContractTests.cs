using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class ContainerLoggingContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string TestsRelativePath = "tests";

    private const string BuilderConstruction = "new PostgreSqlBuilder(";
    private const string LoggerInstallation = "TestcontainersSettings.Logger =";

    private const int MinimumFixtures = 2;

    [Fact]
    public void EveryFixtureThatBuildsAContainerSilencesTheContainerLogger()
    {
        IReadOnlyList<string> fixtures = FixtureSources();

        Assert.True(
            fixtures.Count >= MinimumFixtures,
            $"Only {fixtures.Count} source file(s) under {TestsRelativePath}/ construct a"
                + $" Testcontainers builder, and this tree has {MinimumFixtures}: the DataAccess"
                + " fixture and the end-to-end harness. The walk is not reading the files it is"
                + " about, so every fact below would pass on nothing (F-41).");

        List<string> loud = fixtures
            .Where(path => !File.ReadAllText(path).Contains(LoggerInstallation, StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        Assert.True(
            loud.Count == 0,
            $"These fixture(s) build a container without assigning '{LoggerInstallation}':"
                + $" {Format(loud)}. Testcontainers logs every create, every readiness probe and"
                + " every delete at Information through its own console logger, which on this suite"
                + " is several hundred lines per run and buries the one assertion that failed. What"
                + " diagnoses a container that will not start here is the fixture's own"
                + " DescribeFailure prose, not those lines (F-124).");
    }

    [Fact]
    public void TheLoggerIsSilencedBeforeTheFirstContainerIsBuilt()
    {
        List<string> late = [];

        foreach (string path in FixtureSources())
        {
            string source = File.ReadAllText(path);

            int installed = source.IndexOf(LoggerInstallation, StringComparison.Ordinal);
            int built = source.IndexOf(BuilderConstruction, StringComparison.Ordinal);

            if (installed < 0 || built < 0 || installed < built)
            {
                continue;
            }

            late.Add(Relative(path));
        }

        Assert.True(
            late.Count == 0,
            $"These fixture(s) assign '{LoggerInstallation}' after they construct a builder:"
                + $" {Format(late)}. The setting is read when a container is created, so a container"
                + " built above the assignment logs anyway and the first run of a session is the"
                + " noisy one — which is the run somebody is reading.");
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
