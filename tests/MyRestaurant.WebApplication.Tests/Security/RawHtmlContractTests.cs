using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

public sealed class RawHtmlContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ApplicationSourceDirectory = "src";

    private const string RawHtmlType = "MarkupString";

    private static readonly IReadOnlyDictionary<string, int> RecordedSites =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/MyRestaurant.WebApplication/Components/Account/Pages/EnrollTotp.razor"] = 1,
            ["src/MyRestaurant.WebApplication/Components/Account/Pages/EnrollTotpRequired.razor"] = 1,
            ["src/MyRestaurant.WebApplication/Components/Pages/Administration/TableJoinCode.razor"] = 1,
            ["src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterJoinCode.razor"] = 1,
            ["src/MyRestaurant.WebApplication/Components/Pages/Display/TableDisplay.razor"] = 1,
            ["src/MyRestaurant.WebApplication/Components/Pages/Setup.razor"] = 1,
        };

    [Fact]
    public void TheScanReadsTheApplicationAndFindsItsRawHtml()
    {
        RawHtmlScan scan = Scan();

        Assert.True(
            scan.FilesRead >= 50,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {scan.FilesRead} source file(s) were read under '{ApplicationSourceDirectory}',"
                + $" and this application has well over fifty. The walk is not reading the tree it is"
                + $" about, and the equality below would pass on an empty one."));

        int expected = RecordedSites.Values.Sum();

        Assert.True(
            scan.TotalOccurrences >= expected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{scan.TotalOccurrences} occurrence(s) of {RawHtmlType} were found in code and"
                + $" {expected} are recorded. Fewer than recorded means either a site was deleted"
                + $" without this file being told — delete its entry, that is a good change — or"
                + $" SourceCode.WithoutComments has started removing code, which is the failure mode"
                + $" its own tests exist for."));
    }

    [Fact]
    public void RawHtmlIsProducedAtTheRecordedSitesAndNowhereElse()
    {
        RawHtmlScan scan = Scan();

        List<string> unrecorded = scan.Occurrences
            .Where(site => !RecordedSites.ContainsKey(site.Key))
            .Select(site => Describe(site.Key, site.Value))
            .OrderBy(description => description, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unrecorded.Count == 0,
            $"{unrecorded.Count} file(s) produce raw HTML and are not in RawHtmlContractTests'"
            + $" recorded set: {string.Join("; ", unrecorded)}. §17 claims the raw-HTML sites in this"
            + " tree are a closed set of values this application computed, and that claim is what makes"
            + " script-src 'self' a second line of defence rather than decoration. If the value here can"
            + " never have been typed by a person, add the file to RecordedSites in this commit and say"
            + " why in the slice. If it can, render it as content and let Razor escape it — a guest's"
            + " text reaching raw HTML is the one failure the Content Security Policy cannot catch,"
            + " because the injected markup is served by this application from its own origin.");

        List<string> departed = RecordedSites
            .Where(site => !scan.Occurrences.ContainsKey(site.Key))
            .Select(site => Describe(site.Key, site.Value))
            .OrderBy(description => description, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            departed.Count == 0,
            $"{departed.Count} recorded raw-HTML site(s) no longer produce any:"
            + $" {string.Join("; ", departed)}. Delete the entry. A record that outlives its subject is"
            + " the artefact this file replaced — a census nobody could move — and it is worse here than"
            + " in prose, because it reads as enforced.");

        List<string> miscounted = scan.Occurrences
            .Where(site => RecordedSites.TryGetValue(site.Key, out int recorded) && recorded != site.Value)
            .Select(site => string.Create(
                CultureInfo.InvariantCulture,
                $"{site.Key} produces raw HTML {site.Value} time(s) and {RecordedSites[site.Key]} are recorded"))
            .OrderBy(description => description, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            miscounted.Count == 0,
            $"{miscounted.Count} recorded site(s) disagree with the file: {string.Join("; ", miscounted)}."
            + " A second production in a file that already had one is the cheapest way past this gate and"
            + " the least visible in review, which is exactly why the count is part of the record rather"
            + " than the file alone.");
    }

    private static string Describe(string path, int occurrences)
        => string.Create(CultureInfo.InvariantCulture, $"{path} ({occurrences})");

    private static RawHtmlScan Scan()
    {
        string root = RepositoryRoot();
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        int filesRead = 0;
        int total = 0;

        foreach (string path in SourceFiles(root, ApplicationSourceDirectory))
        {
            filesRead++;

            string code = SourceCode.WithoutComments(File.ReadAllText(path));
            int found = CountOccurrences(code, RawHtmlType);

            if (found == 0)
            {
                continue;
            }

            occurrences[RelativeTo(root, path)] = found;
            total += found;
        }

        return new RawHtmlScan(filesRead, total, occurrences);
    }

    private sealed record RawHtmlScan(
        int FilesRead,
        int TotalOccurrences,
        IReadOnlyDictionary<string, int> Occurrences);

    private static int CountOccurrences(string text, string value)
    {
        int found = 0;

        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static IEnumerable<string> SourceFiles(string root, string directory)
        => Directory
            .EnumerateFiles(Path.Combine(root, directory), "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string RelativeTo(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }
}
