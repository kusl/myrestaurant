using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class ComposeSubstitutionContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";
    private const string ExampleEnvironmentRelativePath = ".env.example";

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
