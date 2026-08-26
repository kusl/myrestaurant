using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class ContainerImageReferenceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string YamlImageKeyName = "image";

    private const string ShellVariableSuffix = "_IMAGE";

    private const string ManagedConstantSuffix = "Image";

    private static readonly string[] UnreadDirectoryNames =
        [".git", ".vs", "bin", "obj", "docs", "node_modules"];

    private const string BareWordRegistry = "localhost";

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

    private static readonly string[] KnownPositions =
    [
        "a YAML image: key",
        "a Containerfile FROM",
        "a shell *_IMAGE assignment",
        "a C# *Image constant",
    ];

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

    private static string? ReadFromOperand(string line)
    {
        if (line.StartsWith('#') || !line.StartsWith("FROM ", StringComparison.Ordinal))
        {
            return null;
        }

        return line["FROM ".Length..].Trim();
    }

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

    private static string FoundHere(IReadOnlyList<ImageReference> references)
        => references.Count == 0
            ? "\n\nThe scan found nothing at all."
            : "\n\nWhat it did find:\n"
              + string.Join("\n", references.Select(reference => "  " + reference.Describe()));

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
