using System.Text.RegularExpressions;
using MyRestaurant.DataAccess.Events;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Events;

/// <summary>
/// The one fact behind F-80: <c>menu_item_event</c>'s vocabulary is declared by a CHECK constraint in a
/// migration and copied into <see cref="EventTypeCatalogue.MenuEventTypes"/>, and the copy went stale
/// two migrations ago without anything noticing (TECHNICAL_SPECIFICATION §7, §8.2, §11.4).
///
/// <para><b>Why nothing noticed, which is the part worth keeping.</b> §11.4's explorer deliberately never
/// <em>refuses</em> a type it does not recognise — a schema this build has not caught up with is exactly
/// the case where an administrator most needs to see the rows — so a missing word costs nothing at run
/// time and shows up only as a filter that cannot be chosen. Every gate in this tree stayed green while
/// two of the menu's verbs were unfilterable on the page whose entire purpose is filtering.</para>
///
/// <para><b>This is F-47's habit applied to a list that had already failed.</b> The rule can be executed:
/// the migrations are files in the tree, the CHECK is the declaration of record, and comparing the two is
/// a set comparison. So the list is derived rather than maintained, and a future <c>0006</c> that widens
/// the vocabulary fails here rather than shipping a dropdown missing a word. It is deliberately <em>not</em>
/// a count — F-77 removed the last count that pretended to govern this vocabulary, and a count would have
/// passed every version of the bug this class exists to catch.</para>
///
/// <para><b>The migrations are read, not the database.</b> This is a unit test with no container:
/// <c>SchemaMigrationRunnerTests</c> owns "the constraint exists on a real PostgreSQL", which is a
/// different question from "the constraint and the C# list agree". Reading the SQL text keeps this fact
/// in the fast suite, where a wrong answer is available in seconds rather than after a container
/// starts.</para>
///
/// <para><b>This class did not compile when it shipped, and the name it could not resolve came from the
/// ledger row that commissioned it (F-83).</b> Every reference below said <c>EventTypeVocabulary</c>.
/// The type has been called <see cref="EventTypeCatalogue"/> since the explorer was written, and so did
/// F-80's rows in <c>DOCUMENTATION_REVIEW.md</c> and Appendix A, and so did Slice 40's own delivery note
/// — four documents naming a class that has never existed in this tree, and this file taking the name
/// from them rather than from the file it is about. The repair is the rename; <b>no gate is added</b>,
/// because the compiler is the gate and it blocked (F-71's ruling, second application). What the
/// blocking cost is the interesting half and it is recorded in §16.4: this class is the repair for F-80,
/// so for one slice the vocabulary was <em>simultaneously</em> correct in <c>EventExplorerReads.cs</c>
/// and unguarded, and nothing said so.</para>
/// </summary>
public sealed class MenuEventVocabularyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string MigrationsRelativePath = "src/MyRestaurant.DataAccess/Migrations";

    /// <summary>
    /// The named constraint every later migration widens by dropping and re-adding. <c>0004</c> created
    /// the name — <c>0001</c> declared its CHECKs inline and PostgreSQL generated the names — and
    /// <c>0005</c> is the first script to drop one by it.
    /// </summary>
    private const string ItemConstraintName = "menu_item_event_type_vocabulary";

    /// <summary>
    /// The picture log's own vocabulary constraint. <c>0006</c> declared it named and <c>0007</c> widened it
    /// by that name, which is the return <c>0006</c> collected on naming every constraint it created.
    /// </summary>
    private const string PictureConstraintName = "menu_item_image_event_type_vocabulary";

    /// <summary>
    /// The surface that renders one sentence per picture event type (§11.4). Named as a path rather than
    /// searched for, because the claim is about <em>this</em> page: it is the only surface in the
    /// application that reads <c>menu_item_image_event</c>, so a second page growing a picture history is a
    /// slice that comes here and says so.
    /// </summary>
    private const string PictureHistorySurface =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    /// <summary>The renderer whose arms are the census (§11.4, F-77).</summary>
    private const string PictureRenderer = "DescribePicture";

    /// <summary>
    /// The schema of record (§8.2). It quotes the DDL the migrations apply, which makes it a
    /// <em>restatement</em> in F-50's sense — joined to its subject only by somebody having written the
    /// vocabulary a second time.
    /// </summary>
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";

    /// <summary>
    /// The <c>event_type IN ( … )</c> list of the <em>last</em> migration that declares
    /// <see cref="ItemConstraintName"/>, which is the vocabulary as it stands after every script has
    /// applied.
    /// Ordered by file name, exactly as DbUp applies them.
    /// </summary>
    [Fact]
    public void TheExplorersMenuVocabulary_IsExactlyWhatTheMigrationsDeclare()
    {
        IReadOnlyList<string> declared = ReadDeclaredVocabulary(ItemConstraintName);

        // Sorted comparison: the C# list is ordered for a human reading a dropdown and the SQL is ordered
        // for a human reading a constraint, and neither ordering is a fact worth asserting. Membership is.
        Assert.Equal(
            declared.Order(StringComparer.Ordinal).ToArray(),
            EventTypeCatalogue.MenuEventTypes.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The catalogue the explorer offers is the concatenation of its three streams, so a menu type that
    /// reached <see cref="EventTypeCatalogue.MenuEventTypes"/> but not
    /// <see cref="EventTypeCatalogue.All"/> would be filterable by hand-edited query string and absent
    /// from the dropdown — which is the same symptom F-80 had, one layer up.
    /// </summary>
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

    /// <summary>
    /// Every picture event type <c>menu_item_image_event</c> admits has a sentence on the one surface that
    /// renders that log (§7, §11.4, <b>F-105</b>).
    ///
    /// <para><b>Why this fact and not a different one.</b> §7's own prose said
    /// <c>attached | replaced | removed</c> and <em>two named biconditionals</em> for a full slice after
    /// <c>0007</c> made the vocabulary four types and the biconditionals three. That is F-77's shape for the
    /// seventh time — a census in prose where nothing looks — and the correction is worth nothing on its own,
    /// because the next migration to widen this vocabulary will be written by somebody who has not read
    /// this paragraph. So the enumeration §7 keeps is <em>a</em> copy and this is the gate: the migrations
    /// are files in the tree, the CHECK is the declaration of record, and comparing it against the surface
    /// is arithmetic on text.</para>
    ///
    /// <para><b>The subject is the surface rather than the write service, and that is deliberate.</b> The
    /// write service's four type constants are <c>private</c>, and widening them to <c>public</c> so that a
    /// test could read them would be changing an API for a test's convenience. The surface is the honest
    /// subject anyway: §11.4 renders the complete stored record and falls back to the raw string for a type
    /// it does not recognise, <em>by design</em>, so a missing arm throws nothing, logs nothing and costs
    /// nothing at run time — it shows up as a cell reading <c>alt_text_changed</c> where a sentence belongs,
    /// on a page an administrator opens once a month. <b>That is F-80's symptom exactly</b>: every gate green
    /// while the page whose purpose is legibility renders a column of column names.</para>
    ///
    /// <para><b>What it does not assert is the other direction</b>, and the omission is F-41's rule rather
    /// than an oversight: an arm for a type the CHECK does not admit is unreachable rather than wrong, the
    /// fallback arm covers it, and a gate forbidding one would report a finding on a surface that had merely
    /// kept a sentence through a migration that narrowed the vocabulary. The non-vacuity guard is that the
    /// renderer is found in the file before any arm is looked for, because <c>Assert.Contains</c> over a
    /// list read out of a regex is precisely the shape that passes against an empty list.</para>
    /// </summary>
    [Fact]
    public void EveryPictureEventType_HasASentenceOnTheSurfaceThatRendersIt()
    {
        IReadOnlyList<string> declared = ReadDeclaredVocabulary(PictureConstraintName);

        string surfacePath = Path.Combine(
            FindRepositoryRoot().FullName,
            PictureHistorySurface.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(surfacePath), $"No surface at '{surfacePath}'.");

        string surface = File.ReadAllText(surfacePath);

        // Non-vacuity, in both halves. A renaming of the renderer must fail here rather than turn every
        // assertion below into a search of a file that no longer renders this log at all (F-41).
        Assert.Contains(PictureRenderer, surface, StringComparison.Ordinal);
        Assert.NotEmpty(declared);

        List<string> missing = [];

        foreach (string type in declared)
        {
            // The arm as this tree writes one: the quoted type in switch position. The leading quote is
            // what keeps this off `"picture-removed"` and `"picture-attached"`, which are the redirect
            // outcomes on the same page and are arms of a different switch.
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

    /// <summary>
    /// Every <b>named</b> <c>event_type</c> vocabulary <c>docs/TECHNICAL_SPECIFICATION.md</c> §8.2 quotes
    /// is the vocabulary the migrations declare, and every one the migrations declare is quoted there
    /// (§8.2, <b>F-111</b>).
    ///
    /// <para><b>Why this is a different claim from the two facts above.</b> Those compare a migration
    /// against C# and against a Razor surface. This compares a migration against the <em>specification</em>,
    /// which §8.2 calls the schema of record and which quotes the DDL in full. That quotation is F-50's
    /// shape exactly — one fact written in two places, joined only by somebody remembering to edit the
    /// second — and it is the copy every future migration is written by, because a person adding a table
    /// reads §8.2 rather than seven `.sql` files.</para>
    ///
    /// <para><b>What made it worth writing is that the neighbouring prose had already drifted.</b> The DDL
    /// blocks were kept current through <c>0004</c>, <c>0005</c>, <c>0006</c> and <c>0007</c>; the note
    /// beneath them describing those same CHECKs still named five event types and two payload columns
    /// against a table that had eight and five, and still stated a testing obligation computed from the
    /// stale figure — an obligation §16.4 separately rules against writing (F-47). The blocks are copied
    /// and the prose is read, so the blocks stayed right. This fact keeps them right by machine instead of
    /// by luck.</para>
    ///
    /// <para><b>Both directions, and the subject is computed on both sides</b> (F-47, F-58): nothing here
    /// names a constraint. A vocabulary the migrations declare and §8.2 never quotes is a table the schema
    /// of record does not document, which is the drift that arrives with the <em>next</em> migration rather
    /// than with an edit to an existing one.</para>
    ///
    /// <para><b>The residual is real and is stated rather than papered over.</b> Only <em>named</em>
    /// constraints are in scope, because that is the only form a text scan can key on — <c>0001</c> and
    /// <c>0003</c> declare their vocabularies inline and unnamed, and <c>menu_section_event</c>'s is
    /// therefore outside this gate. That is not a gap this fact can close; it is the reason <c>0006</c>
    /// began naming every constraint it created, and the reason <c>0008</c> does.</para>
    /// </summary>
    [Fact]
    public void EveryVocabularyTheSpecificationQuotes_IsTheOneTheMigrationsDeclare()
    {
        IReadOnlyDictionary<string, string[]> quoted = ReadNamedVocabularies(
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot().FullName,
                SpecificationRelativePath.Replace('/', Path.DirectorySeparatorChar))));

        IReadOnlyDictionary<string, string[]> declared = ReadMigrationVocabularies();

        // Non-vacuity, both sides. A regex that stopped matching would otherwise satisfy every comparison
        // below by having nothing to compare (F-41).
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

        // The last declaration wins, because a later script drops the constraint and adds it back wider.
        // Ordered by name rather than by write time: DbUp journals and applies by script name, so name
        // order IS application order, and a file system's timestamps are not.
        string? newest = null;

        foreach (FileInfo script in migrations
            .GetFiles("*.sql")
            .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(script.FullName);

            // ADD CONSTRAINT <n> CHECK (event_type IN ('a', 'b', …)) — matched across newlines, since
            // the list is written one line per few types.
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

        // A regex that matched the constraint and found no quoted words would pass the comparison above
        // against an empty C# list, which is the way this gate could assert nothing (F-41).
        Assert.NotEmpty(types);

        return types;
    }

    /// <summary>
    /// Every <c>CONSTRAINT &lt;name&gt; CHECK (event_type IN ( … ))</c> in one body of text, keyed by the
    /// constraint's name. <c>ADD</c> is optional so that one pattern reads a <c>CREATE TABLE</c> body and
    /// an <c>ALTER TABLE</c> statement alike, which is what lets the specification's consolidated DDL be
    /// compared against a migration that widened it.
    /// </summary>
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

            // A named constraint that matched and yielded no quoted word is a parse failure rather than an
            // empty vocabulary, and recording it as the latter is how this gate would assert nothing.
            Assert.NotEmpty(types);

            // Last declaration wins, which is the same rule ReadDeclaredVocabulary applies and is what
            // makes a widened constraint read as its widened self.
            found[match.Groups["name"].Value] = types;
        }

        return found;
    }

    /// <summary>
    /// The same, over every migration in DbUp's apply order — which is file-name order, not the file
    /// system's timestamps.
    /// </summary>
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
