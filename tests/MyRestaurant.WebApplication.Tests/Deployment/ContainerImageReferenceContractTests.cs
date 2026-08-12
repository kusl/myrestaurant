using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

/// <summary>
/// Every container image reference in this repository is fully qualified, and every image name
/// resolves to exactly one reference (TECHNICAL_SPECIFICATION §14.1, §16.4, <b>F-60</b>).
///
/// <para><b>Why this exists, and why it is not a reversal of F-51's ruling.</b> F-51 was the canonical
/// stack refusing to start on a stock Debian because <c>compose.yaml</c> named its images by short
/// name. §14.1 states the rule that came out of it. F-51's row then declined to make that rule
/// executable, and gave the reason: a check for "no <c>image:</c> value lacks a registry component" is
/// a text assertion about a file whose real contract is behavioural, and it would pass on a tree where
/// the images are qualified and the stack still cannot start for the next reason. That reasoning
/// stands, the open item it names — a CI job running the canonical stack on the canonical engine —
/// stays open, and this test is not a substitute for it.</para>
///
/// <para>What this test asserts is a different property, and one that is entirely a property of the
/// tree: that a rule stated for the whole repository is applied at every place in the repository it
/// applies to. It was not. §14.1 said it about <c>compose.yaml</c>, <c>scripts/restore_drill.sh</c>
/// had been doing it since Slice 16, and four other references — both Testcontainers fixtures and both
/// of CI's — were short names. That is F-46's shape for the third time: a rule stated generally and
/// enforced against the examples that prompted it.</para>
///
/// <para><b>What the short names cost, which is the part worth reading.</b> Testcontainers hands the
/// reference to the engine verbatim; its <c>MatchImage.Match</c> records a registry only when the
/// first slash-separated segment contains a <c>.</c> or a <c>:</c>, and the comment beside that line
/// says it "does not resolve or set the default domain and repository prefix". So on a host whose
/// <c>unqualified-search-registries</c> is unpopulated the pull fails — and both fixtures catch every
/// startup failure and turn it into a skip, by design and correctly. The result is not a red suite. It
/// is a green one in which the data-access integration tests and every §16.3 scenario declined
/// to run, reported through a message whose headline said the container engine was unreachable when it
/// was not.</para>
///
/// <para><b>The three positions a reference may occupy</b>, and the reason the set is closed: a
/// reference this scan cannot see is a reference no gate in this project has an opinion about. A YAML
/// <c>image:</c> key, a <c>Containerfile</c> <c>FROM</c> operand, or a value assigned to a name ending
/// in <c>_IMAGE</c> (shell, YAML) or <c>Image</c> (C#). Two references were outside all three when
/// this was written — one spelled into a <c>podman run</c> command line in
/// <c>scripts/quick_tunnel.sh</c>, one passed inline to <c>new PostgreSqlBuilder(…)</c> — and both
/// moved into named constants in the same slice, because naming them is what puts them in scope rather
/// than tidiness.</para>
///
/// <para><b>What it deliberately does not assert.</b> That a reference resolves, that a registry is
/// reachable, or that a tag exists: all three are properties of a host and a network at a moment, and a
/// test that guessed at them would report findings on correct trees (F-41). It also has no opinion
/// about anything under <c>docs/</c>. Those files quote both the correct and the incorrect form on
/// purpose — the whole of F-51's ledger row is about the difference — and a gate that failed on prose
/// describing a defect would be the same mistake in a new place.</para>
///
/// <para>Pure: reads text files off the disk it was built from. No server, no container, no engine.</para>
/// </summary>
public sealed class ContainerImageReferenceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    /// <summary>
    /// The YAML key whose value is an image reference.
    ///
    /// <para>Note the identifier: not one constant in this file is named with a trailing
    /// <c>Image</c>, and that is deliberate rather than stylistic. The scan reads every <c>.cs</c>
    /// file outside the skipped directories, which includes this one, so a constant named for what it
    /// holds — an image key — would be read back as an image reference called "image", which is a
    /// short name and a failure of the fact below. The check being inside its own subject is a
    /// property worth keeping; it just has to be written for.</para>
    /// </summary>
    private const string YamlImageKeyName = "image";

    /// <summary>
    /// The suffix that marks a shell or YAML variable as holding an image reference —
    /// <c>DRILL_POSTGRES_IMAGE</c>, <c>CLOUDFLARED_IMAGE</c>. A convention rather than a list, so a
    /// reference added tomorrow is in scope without this file being edited.
    /// </summary>
    private const string ShellVariableSuffix = "_IMAGE";

    /// <summary>The same convention in C#: <c>PostgreSqlImage</c>.</summary>
    private const string ManagedConstantSuffix = "Image";

    /// <summary>
    /// Directory names never descended into. <c>docs</c> is here for the reason in the class remarks;
    /// the rest are build output and version control. Matched by name at any depth, which would also
    /// skip a <c>src/docs</c> — there is none, and the wider match is the safer error.
    /// </summary>
    private static readonly string[] UnreadDirectoryNames =
        [".git", ".vs", "bin", "obj", "docs", "node_modules"];

    /// <summary>
    /// The one registry component that is a bare word rather than a hostname. Podman and Docker both
    /// treat <c>localhost</c> as a registry rather than as a Docker Hub namespace, so a reference
    /// beginning with it is qualified. Nothing in this tree uses it; it is here because the rule being
    /// asserted is the reference grammar's rule, not a spelling preference.
    /// </summary>
    private const string BareWordRegistry = "localhost";

    /// <summary>
    /// The scan reads every position it knows about. Asserted first and on its own, because both facts
    /// below it are satisfied by an empty set (<b>F-41</b>) — and a renamed constant, a re-indented
    /// workflow, or a <c>Containerfile</c> that grows a stage would produce exactly that in silence.
    /// </summary>
    [Fact]
    public void TheScanFindsImageReferencesInEveryPositionItReads()
    {
        IReadOnlyList<ImageReference> references = ReadImageReferences();

        Assert.True(
            references.Count >= 10,
            $"Only {references.Count} image reference(s) were found in the tree. There are twelve:"
            + " three in compose.yaml, two in Containerfile, two in .github/workflows/ci.yml, three in"
            + " scripts/, and two in the Testcontainers fixtures. If a position changed shape, this"
            + " scan has to follow it rather than be deleted."
            + FoundHere(references));

        foreach (string position in KnownPositions)
        {
            Assert.True(
                references.Any(reference => string.Equals(reference.Position, position, StringComparison.Ordinal)),
                $"The scan found no image reference in position '{position}'. Every fact below reads"
                + " the same list, so a position that stops being read stops being checked without"
                + " anything turning red."
                + FoundHere(references));
        }
    }

    /// <summary>
    /// <b>This is F-60.</b> Every reference names its registry. A reference that does not is resolved
    /// through <c>unqualified-search-registries</c>, which is a per-host file, so a short name means
    /// this repository does not decide which registry it pulls from — and on the canonical host it
    /// decides nothing at all, because a stock Debian ships that setting commented out.
    /// </summary>
    [Fact]
    public void EveryImageReferenceIsFullyQualified()
    {
        IReadOnlyList<ImageReference> references = ReadImageReferences();

        List<ImageReference> shortNames = references
            .Where(reference => !IsFullyQualified(reference.Value))
            .ToList();

        Assert.True(
            shortNames.Count == 0,
            "These image reference(s) name no registry:\n"
            + string.Join("\n", shortNames.Select(reference => "  " + reference.Describe()))
            + "\n\nA reference whose first path segment carries no '.' or ':' is a short name, resolved"
            + " through the host's `unqualified-search-registries`. Fedora's containers-common"
            + " populates that setting and a stock Debian ships it commented out, so a short name is"
            + " a reference this repository has not decided (F-51, F-60). Write it out:"
            + " docker.io/library/postgres:17-alpine, not postgres:17-alpine. On Docker the two forms"
            + " name the same image and the same layer cache, so nothing is given up by being"
            + " explicit.");
    }

    /// <summary>
    /// One image name, one reference. This is the fact that would have caught F-60 as the drift it
    /// was: the two Testcontainers fixtures were pulling <c>postgres:17-alpine</c> while
    /// <c>compose.yaml</c> ran <c>docker.io/library/postgres:17-alpine</c>, so the integration suite
    /// and the canonical stack disagreed about which registry the database came from — and would have
    /// gone on to disagree about the version, which is the same drift with worse consequences and no
    /// symptom at all.
    ///
    /// <para>It is the F-50 pattern with no designated authority, because there is nothing to
    /// nominate: six references to PostgreSQL across a compose file, a workflow, two shell scripts and
    /// two fixtures are all restatements of one another. What the rule can say is that they agree.</para>
    /// </summary>
    [Fact]
    public void EveryImageNameResolvesToExactlyOneReference()
    {
        IReadOnlyList<ImageReference> references = ReadImageReferences();

        List<IGrouping<string, ImageReference>> divergent = references
            .GroupBy(reference => ImageNameOf(reference.Value), StringComparer.Ordinal)
            .Where(group => group
                .Select(reference => reference.Value)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1)
            .ToList();

        Assert.True(
            divergent.Count == 0,
            "These image name(s) are referred to in more than one way:\n"
            + string.Join(
                "\n",
                divergent.Select(group =>
                    $"  {group.Key}:\n"
                    + string.Join("\n", group.Select(reference => "    " + reference.Describe()))))
            + "\n\nOne name, one reference. Two spellings of one image is either a registry"
            + " disagreement or a version disagreement, and the second has no symptom: the suite would"
            + " pass against a database the canonical stack does not run (F-60).");
    }

    // ---------------------------------------------------------------------------------------------
    // Reading the tree. Plain string work, no parser and no regular expressions — the same choice the
    // other Deployment tests make about these same files, and for the same reason.
    // ---------------------------------------------------------------------------------------------

    private static readonly string[] KnownPositions =
    [
        "a YAML image: key",
        "a Containerfile FROM",
        "a shell *_IMAGE assignment",
        "a C# *Image constant",
    ];

    /// <summary>One image reference, and enough about where it is to fix it without searching.</summary>
    private sealed record ImageReference(string RelativePath, int LineNumber, string Position, string Value)
    {
        public string Describe() => $"{Value}  ({RelativePath}:{LineNumber}, {Position})";
    }

    private static IReadOnlyList<ImageReference> ReadImageReferences()
    {
        DirectoryInfo root = FindRepositoryRoot();
        List<ImageReference> references = [];

        foreach (string path in EnumerateReadableFiles(root.FullName))
        {
            string relativePath = Path
                .GetRelativePath(root.FullName, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(path);
            string[] lines = File.ReadAllText(path).Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string? position = null;
                string? raw = null;

                if (string.Equals(fileName, "Containerfile", StringComparison.Ordinal))
                {
                    raw = ReadFromOperand(line);
                    position = "a Containerfile FROM";
                }
                else if (string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
                {
                    raw = ReadYamlImageValue(line);
                    position = "a YAML image: key";
                }
                else if (string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase))
                {
                    raw = ReadShellImageAssignment(line);
                    position = "a shell *_IMAGE assignment";
                }
                else if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    raw = ReadManagedImageConstant(line);
                    position = "a C# *Image constant";
                }

                string? value = raw is null ? null : Unwrap(raw);
                if (value is not null && position is not null)
                {
                    references.Add(new ImageReference(relativePath, index + 1, position, value));
                }
            }
        }

        return references;
    }

    /// <summary>Every file the scan reads, by extension or by the one name that has none.</summary>
    private static IEnumerable<string> EnumerateReadableFiles(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(path);

            bool readable =
                string.Equals(fileName, "Containerfile", StringComparison.Ordinal)
                || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);

            if (readable)
            {
                yield return path;
            }
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (UnreadDirectoryNames.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (string path in EnumerateReadableFiles(child))
            {
                yield return path;
            }
        }
    }

    /// <summary>The operand of a <c>FROM</c> instruction, ignoring any <c>AS stage</c> that follows.</summary>
    private static string? ReadFromOperand(string line)
    {
        if (line.StartsWith('#') || !line.StartsWith("FROM ", StringComparison.Ordinal))
        {
            return null;
        }

        return line["FROM ".Length..].Trim();
    }

    /// <summary>
    /// The value of a mapping whose key is <c>image</c> or ends in <c>_IMAGE</c>. A leading <c>- </c>
    /// is stripped so a sequence entry is read the same way. Anything whose key is not a plain
    /// identifier is not a mapping this cares about, which is what keeps a URL in a comment or a
    /// <c>images:</c> plural out of the results — and what keeps <c>release.yml</c>'s job called
    /// <c>image</c> out of them, since a key with nothing after the colon yields no value.
    /// </summary>
    private static string? ReadYamlImageValue(string line)
    {
        if (line.StartsWith('#'))
        {
            return null;
        }

        string body = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim() : line;

        int colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string key = body[..colon].Trim();
        if (!IsPlainIdentifier(key))
        {
            return null;
        }

        bool interesting =
            string.Equals(key, YamlImageKeyName, StringComparison.Ordinal)
            || key.EndsWith(ShellVariableSuffix, StringComparison.Ordinal);

        return interesting ? body[(colon + 1)..].Trim() : null;
    }

    /// <summary>The right-hand side of <c>NAME=…</c> where <c>NAME</c> ends in <c>_IMAGE</c>.</summary>
    private static string? ReadShellImageAssignment(string line)
    {
        if (line.StartsWith('#'))
        {
            return null;
        }

        int equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return null;
        }

        string name = line[..equals];
        if (!IsPlainIdentifier(name) || !name.EndsWith(ShellVariableSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        return line[(equals + 1)..].Trim();
    }

    /// <summary>
    /// The string literal assigned to an identifier ending in <c>Image</c>. Comment lines are skipped
    /// first, because this file's own remarks name references on purpose and counting them would make
    /// the non-vacuity guard above pass on prose.
    /// </summary>
    private static string? ReadManagedImageConstant(string line)
    {
        if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*'))
        {
            return null;
        }

        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] != '=')
            {
                continue;
            }

            // Skip ==, !=, <=, >=, += and friends: neither side of a comparison is a declaration.
            if (index + 1 < line.Length && line[index + 1] == '=')
            {
                continue;
            }

            if (index > 0 && !char.IsAsciiLetterOrDigit(line[index - 1])
                && line[index - 1] != '_' && line[index - 1] != ' ')
            {
                continue;
            }

            int nameEnd = index;
            while (nameEnd > 0 && line[nameEnd - 1] == ' ')
            {
                nameEnd--;
            }

            int nameStart = nameEnd;
            while (nameStart > 0 && (char.IsAsciiLetterOrDigit(line[nameStart - 1]) || line[nameStart - 1] == '_'))
            {
                nameStart--;
            }

            string name = line[nameStart..nameEnd];
            if (name.Length <= ManagedConstantSuffix.Length
                || !name.EndsWith(ManagedConstantSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            int valueStart = index + 1;
            while (valueStart < line.Length && line[valueStart] == ' ')
            {
                valueStart++;
            }

            if (valueStart >= line.Length || line[valueStart] != '"')
            {
                continue;
            }

            int valueEnd = line.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
            {
                continue;
            }

            return line[(valueStart + 1)..valueEnd];
        }

        return null;
    }

    /// <summary>
    /// One raw right-hand side reduced to the reference it names, or <c>null</c> when it names none.
    /// Handles a trailing comment, surrounding quotes, and the <c>${NAME:-default}</c> form every
    /// overridable value in <c>scripts/</c> is written in. Anything still carrying an unexpanded
    /// variable or a workflow template is not a literal this repository decided, so it is not
    /// reported — that is the same one-direction rule §13's configuration table runs under.
    /// </summary>
    private static string? Unwrap(string raw)
    {
        string value = raw.Trim();

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment].Trim();
        }

        value = Unquote(value);

        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            int fallback = value.IndexOf(":-", StringComparison.Ordinal);
            if (fallback > 0)
            {
                value = Unquote(value[(fallback + 2)..^1].Trim());
            }
        }

        if (value.Length == 0)
        {
            return null;
        }

        value = value.Split(' ', '\t')[0];

        bool unresolved =
            value.Contains("${", StringComparison.Ordinal)
            || value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("{{", StringComparison.Ordinal)
            || value.StartsWith('$')
            || value.Contains("://", StringComparison.Ordinal);

        return unresolved || value.Length == 0 ? null : value;
    }

    private static string Unquote(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed.Trim('"').Trim('\'').Trim();
    }

    /// <summary>
    /// The reference grammar's own rule, which is also the one Testcontainers implements: the first
    /// slash-separated segment is a registry when it contains a <c>.</c> or a <c>:</c>, or when it is
    /// literally <c>localhost</c>. Everything else is a Docker Hub namespace, which is to say a short
    /// name.
    /// </summary>
    private static bool IsFullyQualified(string reference)
    {
        int slash = reference.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        string first = reference[..slash];

        return first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal)
            || string.Equals(first, BareWordRegistry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image's own name: the last path segment, with any tag or digest removed.
    /// <c>docker.io/library/postgres:17-alpine</c> and <c>postgres:17-alpine</c> both give
    /// <c>postgres</c>, which is what makes them comparable.
    /// </summary>
    private static string ImageNameOf(string reference)
    {
        string lastSegment = reference[(reference.LastIndexOf('/') + 1)..];

        int digest = lastSegment.IndexOf('@', StringComparison.Ordinal);
        if (digest > 0)
        {
            lastSegment = lastSegment[..digest];
        }

        int tag = lastSegment.IndexOf(':', StringComparison.Ordinal);
        return tag > 0 ? lastSegment[..tag] : lastSegment;
    }

    private static bool IsPlainIdentifier(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (char character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return char.IsAsciiLetter(candidate[0]) || candidate[0] == '_';
    }

    /// <summary>What the scan did find, appended to a failure so the next reader is not left guessing.</summary>
    private static string FoundHere(IReadOnlyList<ImageReference> references)
        => references.Count == 0
            ? "\n\nThe scan found nothing at all."
            : "\n\nWhat it did find:\n"
              + string.Join("\n", references.Select(reference => "  " + reference.Describe()));

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
