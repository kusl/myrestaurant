using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

/// <summary>
/// No service in the canonical stack waits on another service's <em>health</em>
/// (TECHNICAL_SPECIFICATION §14.1, §16.4, <b>F-53</b>).
///
/// <para><b>Why this exists.</b> podman-compose 1.3.0 — the version Debian trixie ships, and
/// podman-compose is the canonical engine (ADR-0004) — implements <c>up -d</c> as <c>podman run
/// -d</c> for every container <em>followed by</em> a wait on each dependency's <c>depends_on</c>
/// condition, in an unbounded retry loop that logs at debug level and prints nothing. A condition
/// that is never satisfied therefore does not fail: the whole stack starts, the container ids are
/// printed, and the command never returns, with no output naming a cause. That is what
/// <c>scripts/dev_instance.sh</c> hit on its first run — the instance was serving the public
/// internet while the command that started it sat in that loop.</para>
///
/// <para><b>Why the rule is a prohibition rather than a fix.</b> A health status only advances if
/// something runs the healthcheck, and under rootless Podman that is a systemd timer in the user's
/// session — so whether <c>service_healthy</c> is ever satisfied is a property of the host, not of
/// this repository. There is no flag that avoids the wait either: <c>--no-deps</c> is accepted by
/// <c>up</c> in that version and consulted only by <c>run</c>. The only reliable answer is not to
/// ask for the condition, which costs nothing here because
/// <c>SchemaMigrationRunner</c> already retries a connection failure thirty times at two-second
/// intervals (ADR-0012). <c>web</c> losing the race to <c>postgres</c> is a race the application
/// was written to lose safely; the health gate was a convenience it never needed.</para>
///
/// <para><b>Scope, stated so the gaps are deliberate.</b> This asserts one property of one file.
/// It says nothing about whether the images resolve, whether the ports are free, or whether the
/// stack starts — those are behavioural questions about a container engine and belong to a CI job
/// on a Podman host rather than to a string scan (F-41, and the open item F-51's row records). What
/// it does assert is decidable from the text with certainty: which conditions this file asks for.
/// The condition is the thing that hangs, so the condition is the thing gated.</para>
///
/// <para>Pure: reads one file off the disk it was built from. No server, no container, no engine.</para>
/// </summary>
public sealed class ComposeDependencyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";

    /// <summary>The mapping every service is a child of.</summary>
    private const string ServicesMarker = "services:";

    /// <summary>The only condition this file may ask for.</summary>
    private const string PermittedCondition = "service_started";

    /// <summary>
    /// The scan read the file and found the dependency graph. Asserted first and on its own, because
    /// the assertion below it is satisfied by an empty edge set (F-41) — and a compose file that had
    /// been re-indented, or a marker that stopped matching, would produce exactly that in silence.
    /// </summary>
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

    /// <summary>
    /// <b>This is F-53.</b> Every dependency is ordered against the dependency having
    /// <em>started</em>, never against it being <em>healthy</em>, because the canonical engine waits
    /// on the latter forever and silently.
    /// </summary>
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

    /// <summary>
    /// Every dependency names a service this file declares. A typo here is not a compose error on
    /// every engine — some resolve what they can and order what is left arbitrarily — so it is worth
    /// one assertion while the graph is already parsed.
    /// </summary>
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

    // ---------------------------------------------------------------------------------------------
    // Reading the file. Plain string work, no parser and no regular expressions — the same choice
    // ConfigurationSurfaceTests makes about this same file, and for the same reason: a YAML package
    // in the unit test project would be a dependency taken on to read indentation, and the question
    // here is answerable without one.
    //
    // The shape being read, which is the whole of compose's schema that matters to this test:
    //
    //   services:                  <- column 0
    //     web:                     <- column 2, a service name
    //       depends_on:            <- column 4
    //         postgres:            <- column 8, a dependency (mapping form)
    //           condition: ...     <- column 10
    //         - postgres           <- column 8, a dependency (list form; condition is implicit)
    //
    // List form is accepted and recorded as 'service_started', because that is exactly what both
    // engines normalize it to. Failing it would be reporting a finding on a correct file (F-41).
    // ---------------------------------------------------------------------------------------------

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
                // A service name: 'web:' with nothing after the colon.
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
                // A key of the current service. Anything but depends_on closes the block.
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
                // List form. Both engines normalize this to service_started.
                string named = content[2..].Trim();
                if (named.Length > 0)
                {
                    dependencies.Add(new ServiceDependency(currentService, named, PermittedCondition));
                }

                continue;
            }

            if (indent == 6)
            {
                // Mapping form: the dependency's name. Its condition, if any, is the line below.
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

    /// <summary>The text before the first colon, or empty when the line is not 'name:'.</summary>
    private static string NameBeforeColon(string content)
    {
        int colon = content.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return string.Empty;
        }

        return content[..colon].Trim();
    }

    /// <summary>The first line equal to <paramref name="value"/> at or after <paramref name="from"/>.</summary>
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

    /// <summary>
    /// The first line at or after <paramref name="from"/> whose indentation is exactly
    /// <paramref name="indent"/> spaces and which carries content — i.e. where the enclosing block
    /// ends. Returns the line count when the block runs to the end of the file. The same walk
    /// <c>ConfigurationSurfaceTests</c> uses on this file, deliberately.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other contract tests use, and it fails
    /// rather than skips for the same reason: a check that quietly declines to run is worse than none.
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
