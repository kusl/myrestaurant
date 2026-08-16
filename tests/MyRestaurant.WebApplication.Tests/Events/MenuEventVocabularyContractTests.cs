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
    private const string ConstraintName = "menu_item_event_type_vocabulary";

    /// <summary>
    /// The <c>event_type IN ( … )</c> list of the <em>last</em> migration that declares
    /// <see cref="ConstraintName"/>, which is the vocabulary as it stands after every script has applied.
    /// Ordered by file name, exactly as DbUp applies them.
    /// </summary>
    [Fact]
    public void TheExplorersMenuVocabulary_IsExactlyWhatTheMigrationsDeclare()
    {
        IReadOnlyList<string> declared = ReadDeclaredVocabulary();

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

    private static IReadOnlyList<string> ReadDeclaredVocabulary()
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
                $@"ADD\s+CONSTRAINT\s+{ConstraintName}\s+CHECK\s*\(\s*event_type\s+IN\s*\((?<list>[^)]*)\)",
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
