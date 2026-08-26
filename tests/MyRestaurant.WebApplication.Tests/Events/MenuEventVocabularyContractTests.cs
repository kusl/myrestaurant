using System.Text.RegularExpressions;
using MyRestaurant.DataAccess.Events;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Events;

public sealed class MenuEventVocabularyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string MigrationsRelativePath = "src/MyRestaurant.DataAccess/Migrations";

    private const string ItemConstraintName = "menu_item_event_type_vocabulary";

    private const string PictureConstraintName = "menu_item_image_event_type_vocabulary";

    private const string PictureHistorySurface =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    private const string PictureRenderer = "DescribePicture";

    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";

    [Fact]
    public void TheExplorersMenuVocabulary_IsExactlyWhatTheMigrationsDeclare()
    {
        IReadOnlyList<string> declared = ReadDeclaredVocabulary(ItemConstraintName);

        Assert.Equal(
            declared.Order(StringComparer.Ordinal).ToArray(),
            EventTypeCatalogue.MenuEventTypes.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryMenuType_IsOfferedByTheDropdown()
    {
        foreach (string type in EventTypeCatalogue.MenuEventTypes)
        {
            Assert.Contains(type, EventTypeCatalogue.All);
            Assert.True(
                EventTypeCatalogue.IsKnown(type),
                $"'{type}' is in the menu vocabulary and the explorer does not recognise it.");
        }
    }

    [Fact]
    public void EveryPictureEventType_HasASentenceOnTheSurfaceThatRendersIt()
    {
        IReadOnlyList<string> declared = ReadDeclaredVocabulary(PictureConstraintName);

        string surfacePath = Path.Combine(
            FindRepositoryRoot().FullName,
            PictureHistorySurface.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(surfacePath), $"No surface at '{surfacePath}'.");

        string surface = File.ReadAllText(surfacePath);

        Assert.Contains(PictureRenderer, surface, StringComparison.Ordinal);
        Assert.NotEmpty(declared);

        List<string> missing = [];

        foreach (string type in declared)
        {
            if (!surface.Contains($"\"{type}\" =>", StringComparison.Ordinal))
            {
                missing.Add(type);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"{PictureHistorySurface} renders no sentence for: {string.Join(", ", missing)}. §8.2 admits"
                + " the type, so a row carrying it will reach the picture history and be rendered as its"
                + " own stored string — which is legible to nobody and is what F-105 is about. Add an arm"
                + " to " + PictureRenderer + ". If the vocabulary genuinely narrowed, this is the file"
                + " that should record it.");
    }

    [Fact]
    public void EveryVocabularyTheSpecificationQuotes_IsTheOneTheMigrationsDeclare()
    {
        IReadOnlyDictionary<string, string[]> quoted = ReadNamedVocabularies(
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot().FullName,
                SpecificationRelativePath.Replace('/', Path.DirectorySeparatorChar))));

        IReadOnlyDictionary<string, string[]> declared = ReadMigrationVocabularies();

        Assert.True(
            quoted.Count >= 2,
            $"Read {quoted.Count} named event_type vocabularies out of {SpecificationRelativePath}; §8.2"
                + " quotes more than that, so this scan has stopped reading the document.");
        Assert.True(
            declared.Count >= 2,
            $"Read {declared.Count} named event_type vocabularies out of the migrations.");

        Assert.Equal(
            declared.Keys.Order(StringComparer.Ordinal).ToArray(),
            quoted.Keys.Order(StringComparer.Ordinal).ToArray());

        foreach (KeyValuePair<string, string[]> entry in declared)
        {
            Assert.Equal(
                entry.Value.Order(StringComparer.Ordinal).ToArray(),
                quoted[entry.Key].Order(StringComparer.Ordinal).ToArray());
        }
    }

    private static IReadOnlyList<string> ReadDeclaredVocabulary(string constraintName)
    {
        DirectoryInfo migrations = new(
            Path.Combine(FindRepositoryRoot().FullName, MigrationsRelativePath));

        Assert.True(migrations.Exists, $"No migrations directory at '{migrations.FullName}'.");

        string? newest = null;

        foreach (FileInfo script in migrations
            .GetFiles("*.sql")
            .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(script.FullName);

            Match match = Regex.Match(
                text,
                $@"(?:ADD\s+)?CONSTRAINT\s+{constraintName}\s+CHECK\s*\(\s*event_type\s+IN\s*\((?<list>[^)]*)\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(5));

            if (match.Success)
            {
                newest = match.Groups["list"].Value;
            }
        }

        Assert.NotNull(newest);

        string[] types = Regex
            .Matches(newest, @"'(?<type>[^']+)'", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["type"].Value)
            .ToArray();

        Assert.NotEmpty(types);

        return types;
    }

    private static IReadOnlyDictionary<string, string[]> ReadNamedVocabularies(string text)
    {
        Dictionary<string, string[]> found = new();

        foreach (Match match in Regex.Matches(
            text,
            @"(?:ADD\s+)?CONSTRAINT\s+(?<name>[a-z_]+)\s+CHECK\s*\(\s*event_type\s+IN\s*\((?<list>[^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)))
        {
            string[] types = Regex
                .Matches(match.Groups["list"].Value, @"'(?<type>[^']+)'", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(quoted => quoted.Groups["type"].Value)
                .ToArray();

            Assert.NotEmpty(types);

            found[match.Groups["name"].Value] = types;
        }

        return found;
    }

    private static IReadOnlyDictionary<string, string[]> ReadMigrationVocabularies()
    {
        DirectoryInfo migrations = new(
            Path.Combine(FindRepositoryRoot().FullName, MigrationsRelativePath));

        Assert.True(migrations.Exists, $"No migrations directory at '{migrations.FullName}'.");

        Dictionary<string, string[]> found = new();

        foreach (FileInfo script in migrations
            .GetFiles("*.sql")
            .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            foreach (KeyValuePair<string, string[]> entry in ReadNamedVocabularies(
                File.ReadAllText(script.FullName)))
            {
                found[entry.Key] = entry.Value;
            }
        }

        return found;
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
