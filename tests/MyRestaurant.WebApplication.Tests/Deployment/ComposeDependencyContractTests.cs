using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class ComposeDependencyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";

    private const string ServicesMarker = "services:";

    private const string PermittedCondition = "service_started";

    [Fact]
    public void TheScanFindsTheDependencyGraph()
    {
        ComposeGraph graph = ReadComposeGraph();

        Assert.True(
            graph.Services.Count >= 4,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {graph.Services.Count} service(s) were read out of {ComposeRelativePath}. §14.1"
                + $" names four — web, postgres, caddy, cloudflared — so either the file changed shape"
                + $" or the '{ServicesMarker}' block is no longer being found, in which case this scan"
                + $" has to follow it rather than be deleted."));

        Assert.True(
            graph.Dependencies.Count >= 3,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {graph.Dependencies.Count} depends_on edge(s) were read out of"
                + $" {ComposeRelativePath}. Three are expected: web on postgres, and caddy and"
                + $" cloudflared on web. The assertion that no edge waits on health passes vacuously"
                + $" on an empty set, so this one runs first."));
    }

    [Fact]
    public void NoDependencyWaitsOnAnotherServicesHealth()
    {
        ComposeGraph graph = ReadComposeGraph();

        List<ServiceDependency> gated = graph.Dependencies
            .Where(dependency => !string.Equals(dependency.Condition, PermittedCondition, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            gated.Count == 0,
            $"{ComposeRelativePath} gates {Describe(gated)} on a condition other than"
            + $" '{PermittedCondition}'. podman-compose 1.3.0 starts every container and then waits"
            + " for each condition in an unbounded loop that prints nothing, so a condition the host"
            + " never satisfies does not fail the command — it makes 'up -d' never return, with the"
            + " stack already running behind it. That is F-53, and it cost a documented command its"
            + " ability to finish. Waiting for the database to accept connections is the"
            + " application's job: SchemaMigrationRunner retries thirty times at two-second"
            + " intervals, which predates this rule by four milestones.");
    }

    [Fact]
    public void EveryDependencyNamesADeclaredService()
    {
        ComposeGraph graph = ReadComposeGraph();

        List<ServiceDependency> dangling = graph.Dependencies
            .Where(dependency => !graph.Services.Contains(dependency.DependsOn, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            dangling.Count == 0,
            $"{ComposeRelativePath} declares {Describe(dangling)}, and the named service does not"
            + $" exist in this file. Declared services: {string.Join(", ", graph.Services)}.");
    }

    private static string Describe(IReadOnlyList<ServiceDependency> dependencies)
        => string.Join(
            ", ",
            dependencies.Select(dependency =>
                $"'{dependency.Service}' -> '{dependency.DependsOn}' (condition: {dependency.Condition})"));

    private sealed record ServiceDependency(string Service, string DependsOn, string Condition);

    private sealed record ComposeGraph(
        IReadOnlyList<string> Services,
        IReadOnlyList<ServiceDependency> Dependencies);

    private static ComposeGraph ReadComposeGraph()
    {
        string[] lines = ReadRepositoryFile(ComposeRelativePath).Split('\n');

        int servicesStart = IndexOfLine(lines, ServicesMarker, 0);
        if (servicesStart < 0)
        {
            throw new InvalidOperationException(
                $"{ComposeRelativePath} has no line '{ServicesMarker}'. Everything this test reads is"
                + " a child of it.");
        }

        int servicesEnd = IndexOfIndent(lines, servicesStart + 1, 0);

        List<string> services = [];
        List<ServiceDependency> dependencies = [];

        string currentService = "";
        bool insideDependsOn = false;
        string pendingDependency = "";

        for (int index = servicesStart + 1; index < servicesEnd; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (IsBlankOrComment(line))
            {
                continue;
            }

            int indent = IndentOf(line);
            string content = line[indent..];

            if (indent == 2)
            {
                insideDependsOn = false;
                pendingDependency = "";
                currentService = NameBeforeColon(content);
                if (currentService.Length > 0)
                {
                    services.Add(currentService);
                }

                continue;
            }

            if (indent == 4)
            {
                insideDependsOn = string.Equals(content, "depends_on:", StringComparison.Ordinal);
                pendingDependency = "";
                continue;
            }

            if (!insideDependsOn || currentService.Length == 0)
            {
                continue;
            }

            if (indent == 6 && content.StartsWith("- ", StringComparison.Ordinal))
            {
                string named = content[2..].Trim();
                if (named.Length > 0)
                {
                    dependencies.Add(new ServiceDependency(currentService, named, PermittedCondition));
                }

                continue;
            }

            if (indent == 6)
            {
                pendingDependency = NameBeforeColon(content);
                if (pendingDependency.Length > 0)
                {
                    dependencies.Add(
                        new ServiceDependency(currentService, pendingDependency, PermittedCondition));
                }

                continue;
            }

            if (indent >= 8
                && pendingDependency.Length > 0
                && content.StartsWith("condition:", StringComparison.Ordinal))
            {
                string condition = content["condition:".Length..].Trim();
                int last = dependencies.Count - 1;
                if (last >= 0)
                {
                    dependencies[last] = dependencies[last] with { Condition = condition };
                }
            }
        }

        return new ComposeGraph(services, dependencies);
    }

    private static bool IsBlankOrComment(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static int IndentOf(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static string NameBeforeColon(string content)
    {
        int colon = content.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return string.Empty;
        }

        return content[..colon].Trim();
    }

    private static int IndexOfLine(string[] lines, string value, int from)
    {
        for (int index = from; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].TrimEnd('\r'), value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfIndent(string[] lines, int from, int indent)
    {
        for (int index = from; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (line.Length <= indent)
            {
                continue;
            }

            if (indent > 0 && !line.StartsWith(new string(' ', indent), StringComparison.Ordinal))
            {
                continue;
            }

            if (line[indent] != ' ' && line[indent] != '#')
            {
                return index;
            }
        }

        return lines.Length;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                + " §2 describes.");
        }

        return File.ReadAllText(path);
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
