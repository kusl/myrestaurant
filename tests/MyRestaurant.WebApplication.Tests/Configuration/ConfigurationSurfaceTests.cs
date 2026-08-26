using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Configuration;

public sealed class ConfigurationSurfaceTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string OptionsRelativePath = "src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs";
    private const string ComposeRelativePath = "compose.yaml";
    private const string SampleEnvironmentRelativePath = ".env.example";
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";

    private const string BindingMethodMarker = "public static RestaurantOptions FromConfiguration";

    private const string ValidationMethodMarker = "public IReadOnlyList<string> Validate()";

    private const string ValidationMethodTerminator = "public string ResolveWebAuthnRelyingPartyId";

    private const string ConfigurationArgumentMarker = "configuration,";

    private const string WebServiceMarker = "  web:";

    private const string EnvironmentMappingMarker = "    environment:";

    private const string ConfigurationSectionHeading = "## 13. Configuration (environment only)";
    private const string SectionAfterConfigurationHeading = "## 14. Deployment, TLS, origins";

    [Fact]
    public void TheScanFindsTheConfigurationSurface()
    {
        IReadOnlyList<string> keys = ReadConfiguredKeys();

        Assert.True(
            keys.Count >= 12,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {keys.Count} configuration key(s) were read out of {OptionsRelativePath}. Every"
                + $" other assertion in this file passes vacuously on an empty set, so this one runs"
                + $" first. Either the binding method no longer begins '{BindingMethodMarker}', or the"
                + $" read helpers no longer take '{ConfigurationArgumentMarker}' first — in which case"
                + $" this scan has to follow them rather than be deleted."));

        List<string> repeated = keys
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(
            repeated.Count == 0,
            $"{OptionsRelativePath} binds the same key more than once: {string.Join(", ", repeated)}."
            + " Two reads of one variable means two defaults, and the second one wins silently.");
    }

    [Fact]
    public void EveryVariableValidationRefusesIsAVariableTheApplicationReads()
    {
        IReadOnlyList<string> keys = ReadConfiguredKeys();
        IReadOnlyList<string> refused = ReadValidatedKeys();

        Assert.True(
            refused.Count >= 10,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {refused.Count} variable name(s) were found in {OptionsRelativePath}'s"
                + $" Validate(), which is fewer than it refuses to start over. This assertion is the"
                + $" non-vacuity guard for the one below it."));

        List<string> orphans = refused.Where(key => !keys.Contains(key, StringComparer.Ordinal)).ToList();

        Assert.True(
            orphans.Count == 0,
            $"{OptionsRelativePath}'s Validate() names {string.Join(", ", orphans)} in a refusal"
            + " message, and the binding method above it never reads that key. Whichever half was"
            + " renamed, the other half now tells an operator to fix a variable that does nothing.");
    }

    [Fact]
    public void EveryConfiguredKeyReachesTheContainer()
    {
        IReadOnlyList<string> keys = ReadConfiguredKeys();
        IReadOnlyList<string> passed = ReadWebServiceEnvironmentKeys();

        Assert.True(
            passed.Count >= 12,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {passed.Count} environment key(s) were found under the '{WebServiceMarker.Trim()}'"
                + $" service's '{EnvironmentMappingMarker.Trim()}' mapping in {ComposeRelativePath}."
                + $" That is fewer than this application reads, so the block boundaries are being read"
                + $" wrongly rather than the file being wrong."));

        List<string> unreachable = keys.Where(key => !passed.Contains(key, StringComparer.Ordinal)).ToList();

        Assert.True(
            unreachable.Count == 0,
            $"{ComposeRelativePath}'s web service does not pass {string.Join(", ", unreachable)} into"
            + " the container. There is no env_file, so a variable this block does not name does not"
            + " reach the process: the application falls back to its compiled-in default and serves a"
            + " page indistinguishable from a correctly configured one. That is F-50 — it cost the"
            + " AGPL §13 offer its whole point for every fork that followed OPERATIONS §15."
            + " Name it in that block, passing an empty default through, so the compiled-in default"
            + " in RestaurantOptions stays the only place the fallback is written down.");
    }

    [Fact]
    public void EveryConfiguredKeyIsInTheSampleEnvironment()
    {
        IReadOnlyList<string> keys = ReadConfiguredKeys();
        string sample = ReadRepositoryFile(SampleEnvironmentRelativePath);

        List<string> undocumented = keys.Where(key => !AssignsKey(sample, key)).ToList();

        Assert.True(
            undocumented.Count == 0,
            $"{SampleEnvironmentRelativePath} never assigns {string.Join(", ", undocumented)}. That"
            + " file is what an operator copies to .env, so a setting missing from it is a setting"
            + " nobody will know exists. A commented-out line counts.");
    }

    [Fact]
    public void EveryConfiguredKeyIsInTheSpecificationTable()
    {
        IReadOnlyList<string> keys = ReadConfiguredKeys();
        string section = ReadConfigurationSection();

        List<string> unspecified = keys
            .Where(key => !section.Contains("`" + key + "`", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unspecified.Count == 0,
            $"{SpecificationRelativePath} §13 does not list {string.Join(", ", unspecified)}. §13 is"
            + " the normative configuration surface; a variable the program reads and the"
            + " specification does not name is a setting with no contract.");
    }

    private static IReadOnlyList<string> ReadConfiguredKeys()
    {
        string body = ReadSpan(
            ReadRepositoryFile(OptionsRelativePath),
            BindingMethodMarker,
            ValidationMethodMarker,
            OptionsRelativePath);

        List<string> keys = [];
        int cursor = 0;

        while (true)
        {
            int marker = body.IndexOf(ConfigurationArgumentMarker, cursor, StringComparison.Ordinal);
            if (marker < 0)
            {
                break;
            }

            int afterMarker = marker + ConfigurationArgumentMarker.Length;
            int open = body.IndexOf('"', afterMarker);
            if (open < 0)
            {
                break;
            }

            if (!body.AsSpan(afterMarker, open - afterMarker).IsWhiteSpace())
            {
                cursor = afterMarker;
                continue;
            }

            int close = body.IndexOf('"', open + 1);
            if (close < 0)
            {
                break;
            }

            keys.Add(body[(open + 1)..close]);
            cursor = close + 1;
        }

        return keys;
    }

    private static IReadOnlyList<string> ReadValidatedKeys()
    {
        string body = ReadSpan(
            ReadRepositoryFile(OptionsRelativePath),
            ValidationMethodMarker,
            ValidationMethodTerminator,
            OptionsRelativePath);

        HashSet<string> names = new(StringComparer.Ordinal);

        for (int index = 0; index < body.Length; index++)
        {
            if (!char.IsAsciiLetterUpper(body[index]))
            {
                continue;
            }

            if (index > 0 && IsIdentifierCharacter(body[index - 1]))
            {
                continue;
            }

            int end = index;
            while (end < body.Length && IsScreamingCaseCharacter(body[end]))
            {
                end++;
            }

            string candidate = body[index..end];
            index = end - 1;

            if (candidate.Length >= 5 && candidate.Contains('_', StringComparison.Ordinal)
                && !candidate.EndsWith('_'))
            {
                names.Add(candidate);
            }
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character == '_';

    private static bool IsScreamingCaseCharacter(char character)
        => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_';

    private static IReadOnlyList<string> ReadWebServiceEnvironmentKeys()
    {
        string[] lines = ReadRepositoryFile(ComposeRelativePath).Split('\n');

        int serviceStart = IndexOfLine(lines, WebServiceMarker, 0);
        if (serviceStart < 0)
        {
            throw new InvalidOperationException(
                $"{ComposeRelativePath} has no line '{WebServiceMarker}'. The canonical stack's web"
                + " service is what this test is about; if it was renamed, this test moves with it.");
        }

        int serviceEnd = IndexOfIndent(lines, serviceStart + 1, 2);
        int mappingStart = IndexOfLine(lines, EnvironmentMappingMarker, serviceStart + 1);
        if (mappingStart < 0 || mappingStart >= serviceEnd)
        {
            throw new InvalidOperationException(
                $"{ComposeRelativePath}'s web service has no '{EnvironmentMappingMarker.Trim()}'"
                + " mapping. Every configured variable reaches the process through it.");
        }

        int mappingEnd = Math.Min(IndexOfIndent(lines, mappingStart + 1, 4), serviceEnd);

        List<string> keys = [];
        for (int index = mappingStart + 1; index < mappingEnd; index++)
        {
            string line = lines[index];
            if (!line.StartsWith("      ", StringComparison.Ordinal))
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string key = line[6..colon];
            if (key.Length > 0 && key.All(IsIdentifierCharacter))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static bool AssignsKey(string text, string key)
    {
        foreach (string line in text.Split('\n'))
        {
            string candidate = line.TrimStart();
            while (candidate.StartsWith('#'))
            {
                candidate = candidate[1..].TrimStart();
            }

            if (candidate.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadConfigurationSection()
        => ReadSpan(
            ReadRepositoryFile(SpecificationRelativePath),
            ConfigurationSectionHeading,
            SectionAfterConfigurationHeading,
            SpecificationRelativePath);

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
        string prefix = new(' ', indent);

        for (int index = from; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (line.Length <= indent || !line.StartsWith(prefix, StringComparison.Ordinal))
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

    private static string ReadSpan(string text, string from, string to, string what)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"'{from}' does not occur in {what}, so the span this test reads could not be found.");
        }

        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException(
                $"'{to}' does not occur after '{from}' in {what}, so the span this test reads has no"
                + " end. It is bounded on purpose: an unbounded scan would read the whole file and"
                + " report whatever it found there.");
        }

        return text[start..end];
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
