using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

/// <summary>
/// Copying <c>.env.example</c> to <c>.env</c> supplies every variable <c>compose.yaml</c> interpolates
/// (TECHNICAL_SPECIFICATION §14.1, §16.4, <b>F-57</b>).
///
/// <para><b>Why this exists.</b> Every value in <c>compose.yaml</c> is written
/// <c>${NAME:-default}</c>, and on Debian trixie's podman-compose — which ADR-0004 calls the canonical
/// engine — the branch after <c>:-</c> is not applied. Every variable that was not already set in the
/// environment reached its container as the placeholder text: the application printed five
/// <c>Configuration error:</c> lines naming values like
/// <c>'${RESTAURANT_TIME_ZONE:-America/New_York}'</c> and exited 1, and <c>POSTGRES_USER</c> reached
/// <c>initdb</c> as punctuation, so the bootstrap statement failed, initdb wiped the data directory,
/// and the container crash-looped. One engine behaviour, two dead containers.</para>
///
/// <para><b>Why this file is the assertion.</b> The remediation is to supply the variables rather than
/// to rely on the defaults, and the documented way to supply them is
/// <c>cp .env.example .env</c> (OPERATIONS §2). That instruction is only true if
/// <c>.env.example</c> actually <em>assigns</em> every variable the stack interpolates — and when the
/// finding was made it assigned nineteen of twenty-two, leaving <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>,
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> and <c>CLOUDFLARE_TUNNEL_TOKEN</c> commented out. A commented-out
/// line supplies nothing. So this is F-50's pattern once more: <c>compose.yaml</c> is the authoritative
/// statement of what needs supplying, <c>.env.example</c> is the restatement, and the restatement is
/// what stops being true when somebody adds a setting.</para>
///
/// <para><b>Why an empty assignment is not the same as a commented-out one</b>, which is the part worth
/// having a test about. <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>'s emptiness is what switches the exporter off
/// (<c>Program.cs</c> attaches no OTLP exporter when it is blank). Commented out, the literal
/// <c>${OTEL_EXPORTER_OTLP_ENDPOINT:-}</c> arrives instead — which is <em>not</em> blank, so the
/// exporter is switched on and pointed at a hostname made of braces. The setting whose whole purpose is
/// to be absent is the one that fails loudest when it is merely unwritten.</para>
///
/// <para><b>Scope, stated so the gaps are deliberate.</b> Only placeholders inside an
/// <c>environment:</c> mapping are in scope — those are what a container is handed.
/// <c>SOURCE_REVISION</c> appears under <c>build.args</c> and is excluded: it is stamped by a pipeline
/// or by <c>scripts/dev_instance.sh</c>, and <c>Containerfile</c>'s own <c>ARG</c> is where its
/// fallback is decided (F-50's ruling). This test says nothing about whether any particular engine
/// applies defaults — that is a property of a host, it is not decidable from the text, and
/// <c>scripts/check_compose_substitution.sh</c> asks the engine directly.</para>
///
/// <para>Pure: reads two files off the disk it was built from. No server, no container, no engine.</para>
/// </summary>
public sealed class ComposeSubstitutionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";
    private const string ExampleEnvironmentRelativePath = ".env.example";

    /// <summary>
    /// The scan read both sides. Asserted first and on its own, because every assertion below it is
    /// satisfied by an empty placeholder set (<b>F-41</b>) — and a re-indented <c>environment:</c>
    /// block would produce exactly that in silence.
    /// </summary>
    [Fact]
    public void TheScanFindsBothSides()
    {
        IReadOnlyList<string> interpolated = ReadInterpolatedEnvironmentVariables();
        IReadOnlyDictionary<string, string> assigned = ReadExampleAssignments();

        Assert.True(
            interpolated.Count >= 20,
            $"Only {interpolated.Count} interpolated variable(s) were read out of"
            + $" {ComposeRelativePath}'s environment mappings. There are twenty-two; if that block"
            + $" changed shape, this scan has to follow it rather than be deleted.");

        Assert.True(
            assigned.Count >= 20,
            $"Only {assigned.Count} assignment(s) were read out of {ExampleEnvironmentRelativePath}."
            + $" The assertion below passes vacuously against an empty set, so this one runs first.");
    }

    /// <summary>
    /// <b>This is F-57.</b> Every variable the stack interpolates is assigned in
    /// <c>.env.example</c>, so that copying it leaves nothing depending on the engine applying a
    /// default.
    /// </summary>
    [Fact]
    public void EveryInterpolatedVariableIsAssignedInTheExampleEnvironment()
    {
        IReadOnlyList<string> interpolated = ReadInterpolatedEnvironmentVariables();
        IReadOnlyDictionary<string, string> assigned = ReadExampleAssignments();

        List<string> unsupplied = interpolated
            .Where(name => !assigned.ContainsKey(name))
            .ToList();

        Assert.True(
            unsupplied.Count == 0,
            $"{ExampleEnvironmentRelativePath} does not assign: {string.Join(", ", unsupplied)}."
            + $" {ComposeRelativePath} interpolates each of them, and on an engine that does not apply"
            + $" the default after ':-' the placeholder text reaches the container instead — which is"
            + $" what left the application refusing its own configuration and initdb wiping its data"
            + $" directory (F-57). A commented-out line supplies nothing: OPERATIONS §2 says to copy"
            + $" this file, so every variable the stack interpolates has to be assigned in it, empty if"
            + $" that is the right value.");
    }

    /// <summary>
    /// A variable whose <c>compose.yaml</c> default is empty is assigned <em>empty</em> here, not given
    /// a value. Derived from the compose file rather than listed, and the reason is
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: an empty endpoint attaches no exporter, so a plausible
    /// example value in this file would switch OpenTelemetry export on for everybody who followed the
    /// runbook.
    /// </summary>
    [Fact]
    public void VariablesWhoseComposeDefaultIsEmptyAreAssignedEmpty()
    {
        IReadOnlyList<string> emptyByDefault = ReadVariablesWithEmptyComposeDefault();
        IReadOnlyDictionary<string, string> assigned = ReadExampleAssignments();

        Assert.True(
            emptyByDefault.Count >= 3,
            $"Only {emptyByDefault.Count} variable(s) in {ComposeRelativePath} have an empty default."
            + $" Three are expected — the two OTEL_* exporter settings and the tunnel token — so this"
            + $" scan is no longer finding what it is about.");

        List<string> given = emptyByDefault
            .Where(name => assigned.TryGetValue(name, out string? value) && value.Length > 0)
            .ToList();

        Assert.True(
            given.Count == 0,
            $"{ExampleEnvironmentRelativePath} gives a value to {string.Join(", ", given)}, and"
            + $" {ComposeRelativePath} defaults each of them to empty. Empty is the setting: an empty"
            + $" OTEL_EXPORTER_OTLP_ENDPOINT is what makes the application attach no OTLP exporter, so"
            + $" a value here would turn export on for every operator who copied this file. Assign it"
            + $" empty and put the example in a comment beside it.");
    }

    // ---------------------------------------------------------------------------------------------
    // Reading the two files. Plain string work, no parser and no regular expressions — the same
    // choice ConfigurationSurfaceTests and ComposeDependencyContractTests make about this same
    // compose file, and for the same reason.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every <c>${NAME…}</c> that appears inside an <c>environment:</c> mapping, in any service.
    /// Bounded to those mappings deliberately: they are what a container is handed, and
    /// <c>build.args</c>' <c>SOURCE_REVISION</c> has its fallback decided in <c>Containerfile</c>.
    /// </summary>
    private static IReadOnlyList<string> ReadInterpolatedEnvironmentVariables()
    {
        List<string> names = [];

        foreach (string line in ReadEnvironmentMappingLines())
        {
            foreach (string name in ReadPlaceholderNames(line))
            {
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// The variables written <c>${NAME:-}</c> — a default of nothing at all — inside an
    /// <c>environment:</c> mapping.
    /// </summary>
    private static IReadOnlyList<string> ReadVariablesWithEmptyComposeDefault()
    {
        List<string> names = [];

        foreach (string line in ReadEnvironmentMappingLines())
        {
            foreach (string name in ReadPlaceholderNames(line))
            {
                if (line.Contains($"${{{name}:-}}", StringComparison.Ordinal)
                    && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// The lines of every <c>environment:</c> mapping in the file, across all services. The shape
    /// being read:
    /// <code>
    /// services:
    ///   web:
    ///     environment:
    ///       RESTAURANT_NAME: ${RESTAURANT_NAME:-My Restaurant}
    /// </code>
    /// </summary>
    private static IReadOnlyList<string> ReadEnvironmentMappingLines()
    {
        string[] lines = ReadRepositoryFile(ComposeRelativePath).Split('\n');

        int servicesStart = IndexOfLine(lines, "services:", 0);
        if (servicesStart < 0)
        {
            throw new InvalidOperationException(
                $"{ComposeRelativePath} has no line 'services:'. Everything this test reads is a child"
                + " of it.");
        }

        int servicesEnd = IndexOfIndent(lines, servicesStart + 1, 0);

        List<string> collected = [];
        bool insideEnvironment = false;

        for (int index = servicesStart + 1; index < servicesEnd; index++)
        {
            string line = lines[index].TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int indent = IndentOf(line);

            if (indent <= 2)
            {
                insideEnvironment = false;
                continue;
            }

            if (indent == 4)
            {
                insideEnvironment = string.Equals(line[indent..], "environment:", StringComparison.Ordinal);
                continue;
            }

            if (insideEnvironment)
            {
                collected.Add(line[indent..]);
            }
        }

        return collected;
    }

    /// <summary>The <c>NAME</c> of every <c>${NAME…}</c> in one line of text.</summary>
    private static IReadOnlyList<string> ReadPlaceholderNames(string line)
    {
        List<string> names = [];
        int position = 0;

        while (true)
        {
            int open = line.IndexOf("${", position, StringComparison.Ordinal);
            if (open < 0)
            {
                return names;
            }

            int start = open + 2;
            int end = start;
            while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] == '_'))
            {
                end++;
            }

            if (end > start)
            {
                names.Add(line[start..end]);
            }

            position = end;
        }
    }

    /// <summary>
    /// Every uncommented <c>NAME=value</c> in <c>.env.example</c>, mapped to its value. A commented
    /// line is deliberately not an assignment — that distinction is the whole finding.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadExampleAssignments()
    {
        Dictionary<string, string> assignments = new(StringComparer.Ordinal);

        foreach (string rawLine in ReadRepositoryFile(ExampleEnvironmentRelativePath).Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string name = line[..equals].Trim();
            if (name.Length == 0 || !IsVariableName(name))
            {
                continue;
            }

            assignments[name] = line[(equals + 1)..].Trim();
        }

        return assignments;
    }

    private static bool IsVariableName(string candidate)
    {
        foreach (char character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return char.IsAsciiLetter(candidate[0]) || candidate[0] == '_';
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
    /// ends. The same walk the other two Deployment tests use on this file, deliberately.
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
