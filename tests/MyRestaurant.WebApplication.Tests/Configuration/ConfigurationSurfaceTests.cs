using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Configuration;

/// <summary>
/// Every environment variable the application reads reaches a deployed container, and is written down
/// where an operator will look for it (TECHNICAL_SPECIFICATION §13, §16.4, F-50).
///
/// <para><b>Why this exists.</b> <c>compose.yaml</c>'s <c>web</c> service enumerates its environment
/// key by key. There is no <c>env_file</c>, so a variable that is not named there does not reach the
/// process — no error, no warning, no log line. The application then uses its compiled-in default and
/// serves a page that looks exactly like a correctly configured one. F-50 is what that costs when the
/// variable in question is <c>RESTAURANT_SOURCE_URL</c>: an operator who forked this program, set the
/// variable in <c>.env</c> exactly as OPERATIONS §15 instructs, and deployed through the only
/// deployment path this project documents, published an AGPL §13 offer pointing at somebody else's
/// repository. Four documents agreed and the transport between them dropped the value on the floor —
/// which is F-38's shape, one layer out.</para>
///
/// <para><b>Why the subject is derived and not listed.</b> <c>RestaurantOptions</c>'s binding
/// method is the authoritative statement of what this application reads; §13's table, <c>.env.example</c>
/// and <c>compose.yaml</c> are three restatements of it, and a restatement is exactly the kind of thing
/// that stops being true when somebody adds a setting. So the key set is read out of the method rather
/// than written here (F-47's habit, seventh application), and the three restatements are checked
/// against it. Adding a variable and forgetting any one of the three fails this file by name.</para>
///
/// <para><b>Scope, stated so the gaps are deliberate.</b> The subject is
/// <c>RestaurantOptions.FromConfiguration</c> — which §13 calls the complete environment-only
/// configuration of <em>this application</em>. The <c>OTEL_*</c> variables are outside it on purpose:
/// they are read by the OpenTelemetry SDK under its own published contract, this tree never names them
/// in a binding call, and asserting a third party's variable list here would be this project taking
/// responsibility for a surface it does not own. The reverse direction — a key in <c>compose.yaml</c>
/// that nothing reads — is deliberately <em>not</em> asserted for the same reason: <c>POSTGRES_*</c>
/// is consumed by the database image and <c>OTEL_*</c> by the exporter, so that assertion would report
/// findings on a correct tree (F-41).</para>
///
/// <para>Pure: reads files off the disk it was built from. No server, no container, no engine.</para>
/// </summary>
public sealed class ConfigurationSurfaceTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string OptionsRelativePath = "src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs";
    private const string ComposeRelativePath = "compose.yaml";
    private const string SampleEnvironmentRelativePath = ".env.example";
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";

    /// <summary>Where the binding method begins. Everything read as a key comes from inside it.</summary>
    private const string BindingMethodMarker = "public static RestaurantOptions FromConfiguration";

    /// <summary>The first thing after the binding method. Bounds the scan so nothing else is read.</summary>
    private const string BindingMethodTerminator = "/// <summary>Returns a human-readable reason";

    /// <summary>Where the fail-fast validation begins.</summary>
    private const string ValidationMethodMarker = "public IReadOnlyList<string> Validate()";

    /// <summary>The first thing after the validation method.</summary>
    private const string ValidationMethodTerminator = "public string ResolveWebAuthnRelyingPartyId";

    /// <summary>
    /// The argument every read helper takes first. The key is the next string literal after it — which
    /// is true of <c>ReadString</c>, <c>ReadInt</c> and <c>ReadOriginPatterns</c> alike, and stays true
    /// of a fourth helper written in the same shape.
    /// </summary>
    private const string ConfigurationArgumentMarker = "configuration,";

    /// <summary>The service whose environment a deployed instance actually gets.</summary>
    private const string WebServiceMarker = "  web:";

    /// <summary>The mapping inside that service. Keys are its two-further-indented children.</summary>
    private const string EnvironmentMappingMarker = "    environment:";

    /// <summary>§13's heading, and the heading after it. The table between them is the documentation.</summary>
    private const string ConfigurationSectionHeading = "## 13. Configuration (environment only)";
    private const string SectionAfterConfigurationHeading = "## 14. Deployment, TLS, origins";

    /// <summary>
    /// The scan read the tree and classified it. Asserted first and on its own, because every
    /// assertion below is satisfied by an empty key set (F-41) — and a marker string that stopped
    /// matching would produce exactly that, silently.
    /// </summary>
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

    /// <summary>
    /// Every variable the process refuses to start over is a variable it actually reads. A second,
    /// independent observation of the same set from the same file — because a rename applied to the
    /// binding call and not to the refusal message produces an error message naming a variable nobody
    /// can set, and nothing else in the suite would notice.
    /// </summary>
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

    /// <summary>
    /// <b>This is F-50.</b> Every key the application reads is passed into the <c>web</c> service by
    /// <c>compose.yaml</c> — the canonical stack (§14.1), and the only deployment path OPERATIONS
    /// documents. A key absent from that block is a setting an operator can put in <c>.env</c> and
    /// never see take effect.
    /// </summary>
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

    /// <summary>
    /// Every key is in <c>.env.example</c>, which is the file an operator copies. Commented-out lines
    /// count: showing a variable and its default without switching it on is how this file documents an
    /// optional setting.
    /// </summary>
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

    /// <summary>
    /// Every key is in §13's table, which is the normative list. Checked against the section rather
    /// than the whole document, so a variable mentioned in passing elsewhere does not count as
    /// specified.
    /// </summary>
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

    // ---------------------------------------------------------------------------------------------
    // Reading the tree. Plain string work, no regular expressions — the same choice the rest of this
    // suite makes, and the reason every scan here can say exactly what it looked at.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The keys bound by <c>FromConfiguration</c>, in source order. A key is the first string literal
    /// after a <c>configuration,</c> argument, and the span between the two must be whitespace — so a
    /// call shaped differently is skipped rather than guessed at.
    /// </summary>
    private static IReadOnlyList<string> ReadConfiguredKeys()
    {
        string body = ReadSpan(
            ReadRepositoryFile(OptionsRelativePath),
            BindingMethodMarker,
            BindingMethodTerminator,
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

    /// <summary>
    /// The variable names <c>Validate</c> puts in refusal messages. Every maximal run of upper-case
    /// ASCII, digits and underscores that begins at a token boundary, contains an underscore, and is
    /// long enough not to be an abbreviation.
    /// </summary>
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

    /// <summary>
    /// The environment keys of the <c>web</c> service. The service block runs from its own two-space
    /// key to the next one; the mapping runs from its four-space key to the next one; the keys are the
    /// six-space children between. Bounded that way so a key set on a <em>different</em> service does
    /// not count — which is a real failure mode and not a hypothetical one, since every service in the
    /// file takes an <c>environment:</c> block.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="text"/> assigns <paramref name="key"/> at the start of a line,
    /// optionally behind a comment marker.
    /// </summary>
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
    /// ends. Returns the line count when the block runs to the end of the file.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other contract tests use, and it fails rather
    /// than skips for the same reason: a check that quietly declines to run is worse than none.
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
