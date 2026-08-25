using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

/// <summary>
/// Raw HTML has a closed set of sources in this application, and none of them is a person
/// (TECHNICAL_SPECIFICATION §11.11, §16.4, §17; <c>MENU_AND_HANDHELD_PLAN.md</c> Stage 6b; ADR-0014).
///
/// <para><b>Why this exists.</b> §17 has claimed since Slice 24 that <c>script-src 'self'</c> with no
/// hash, nonce or <c>unsafe-*</c> is a second line of defence <em>because</em> the raw-HTML sites in this
/// tree are a small closed set that Razor's escaping is deliberately bypassed at. That claim was written
/// in three places as a count — <em>the six <c>MarkupString</c> sites</em> — and enforced in none, which
/// is the artefact this project has now met under F-73, F-77, F-89, F-105, F-111 and F-112: a census in
/// prose, correct on the day it was written, with nothing able to move it. <b>The count is deleted and
/// the set is enforced instead.</b></para>
///
/// <para><b>What makes it urgent rather than tidy is the next feature.</b>
/// <c>MENU_AND_HANDHELD_PLAN.md</c>'s Stage 6 is guest comments, and it is the first content in this
/// system that one guest writes for another guest to read. Its prerequisite 4 is exactly this rule —
/// comment text goes through Razor's default encoding and must never reach a <c>MarkupString</c> — and
/// ADR-0014 already states the same thing about an item description. Both are sentences. A seventh raw
/// HTML site is one line of markup that compiles, renders, looks like every other line around it, and
/// turns an escaped field into an injection point; the policy would keep an injected script inert and
/// would keep nothing else inert. <b>So the prerequisite is discharged before the feature exists</b>,
/// which is the shape Stage 6a used for prerequisite 1: the cheapest moment to make a rule executable is
/// while nothing yet depends on it being wrong.</para>
///
/// <para><b>Why it is a recorded set and not a rule about what may be cast.</b> Whether a value is
/// person-authored is not decidable from text — it is a fact about where the value came from, several
/// calls away — so a gate claiming to assert it would be reaching past what it can decide (F-41). What
/// <em>is</em> decidable is <em>where</em> raw HTML is produced, and a closed set turns the undecidable
/// question into a human one asked at the right moment: adding a site means editing
/// <see cref="RecordedSites"/>, in the same commit, with the reason in the slice. That is the same trade
/// <c>ContentSecurityPolicyContractTests</c> makes about its two concessions.</para>
///
/// <para><b>It reads code rather than text</b> (<b>F-116</b>), through <see cref="SourceCode"/>. That is
/// load-bearing rather than defensive: <c>ResponseSecurityHeaders</c>' own summary names the type in
/// prose while explaining what the policy is for, so a scan of the raw text would report a finding on
/// the file that documents the rule. F-67's open-parenthesis distinction cannot help here — the type name
/// is not a call and there is nothing to key on — which is why the reader had to become comment-blind
/// rather than the pattern become cleverer.</para>
///
/// <para><b>Pure.</b> Reads two directories of source text off the disk it was built from. No server, no
/// container, no browser.</para>
/// </summary>
public sealed class RawHtmlContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ApplicationSourceDirectory = "src";

    /// <summary>
    /// The type that bypasses Razor's escaping. Named once, because the scan below and the failure
    /// messages that explain it must be talking about the same thing.
    /// </summary>
    private const string RawHtmlType = "MarkupString";

    /// <summary>
    /// Every place in this application where raw HTML is produced, and how many times in each.
    ///
    /// <para><b>All six render an SVG QR code this application computed from a value it minted</b> — a
    /// rotating join token (§4.3), a pairing code (§4.2), or a TOTP enrolment URI (§3.4). None of them
    /// renders anything a person typed, which is the property the set exists to keep true, and none of
    /// them can be given one without this file changing.</para>
    ///
    /// <para><b>A seventh entry is a decision and not a formality.</b> The question to answer before
    /// adding one is not <em>is this string safe today</em> but <em>can a person ever reach it</em> — a
    /// display name, a customization note, an item description, a comment. If the answer is yes the
    /// answer is no: render the value as content and let Razor escape it. If the markup genuinely has to
    /// be constructed, construct it here from values this application produced, the way the QR renderer
    /// does, rather than concatenating a caller's string into it.</para>
    ///
    /// <para>Paths are repo-relative with forward slashes, matching how every other gate in this
    /// repository reports a file.</para>
    /// </summary>
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

    /// <summary>
    /// The scan opened the application and found raw HTML in it.
    ///
    /// <para>Asserted on its own and first, because the fact below is an equality against a set that
    /// would also be satisfied by a walk that read nothing and a reader that returned nothing — and both
    /// of those are one typo away, since the walk is a path and the reader strips text (F-41).</para>
    /// </summary>
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

    /// <summary>
    /// Raw HTML is produced at the recorded sites, exactly as often as recorded, and nowhere else.
    ///
    /// <para>An equality rather than a subset, in both directions on purpose. A site the record does not
    /// have is the case this gate was built for. A recorded site the tree no longer has is the case that
    /// makes the record rot — it is how a list of six becomes a list of five real entries and one
    /// superstition, and the next reader trusts the superstition.</para>
    /// </summary>
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

    /// <summary>
    /// Every file under <c>src/</c> that names <see cref="RawHtmlType"/> in code, by repo-relative path,
    /// with how many times.
    /// </summary>
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

    /// <summary>What one walk of the application found, so that two facts read the tree the same way.</summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> every other contract test in this repository uses,
    /// and it throws rather than skips for the same reason: a check that quietly declines to run is worse
    /// than none.
    /// </summary>
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
