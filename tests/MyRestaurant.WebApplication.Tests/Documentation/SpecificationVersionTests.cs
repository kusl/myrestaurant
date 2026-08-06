using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// The specification says the same version at both ends of itself
/// (TECHNICAL_SPECIFICATION §16.4, §18).
///
/// <para><b>Why this is a test and not a habit.</b> It has now drifted twice. The v1.3 changelog entry
/// carries the correction in its own words — <em>"the header of this document read v1.1 while the
/// changelog below already carried a v1.2 entry — Slice 16 bumped one and not the other"</em> — and
/// Slice 22 did the identical thing, shipping a v1.7 entry under a header reading v1.6. Noticing it a
/// second time and correcting it a second time would be the ledger recording a habit where it should
/// be naming something executable (F-38's rule, and F-48).</para>
///
/// <para><b>Why it is small.</b> The failure is cheap and the check should be cheaper. It asserts two
/// things and refuses to grow a third: that the header agrees with the newest entry, and that the
/// entries descend — without which "newest" is not a property of the file at all, it is a property of
/// whoever last edited the top of the list.</para>
///
/// <para>It deliberately does not check dates, section numbers, or that a changelog entry exists for
/// the current commit. Those are judgements about content; this is arithmetic about one string, and a
/// gate that reaches past what it can decide is a gate that reports findings on correct trees
/// (F-41).</para>
/// </summary>
public sealed class SpecificationVersionTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string SpecificationRelativePath = "docs/TECHNICAL_SPECIFICATION.md";

    /// <summary>The header's marker. The version is the token immediately after it.</summary>
    private const string HeaderMarker = "**Version ";

    /// <summary>Where the entries start. Nothing above this line is parsed as one.</summary>
    private const string ChangelogHeading = "## Changelog";

    /// <summary>An entry's marker at the start of a line: <c>**v1.8 — …</c>.</summary>
    private const string EntryMarker = "**v";

    [Fact]
    public void TheHeaderVersionMatchesTheNewestChangelogEntry()
    {
        string text = ReadSpecification();
        Version header = ReadHeaderVersion(text);
        IReadOnlyList<Version> entries = ReadChangelogVersions(text);

        Assert.NotEmpty(entries);

        Assert.True(
            header == entries[0],
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SpecificationRelativePath} says 'Version {header}' in its header and its newest"
                + $" changelog entry is v{entries[0]}. One of the two was bumped and the other was not,"
                + $" which has now happened twice — see the v1.3 entry, which corrects the same drift"
                + $" from Slice 16. Whichever is right, both have to say it."));
    }

    [Fact]
    public void TheChangelogEntriesDescend()
    {
        string text = ReadSpecification();
        IReadOnlyList<Version> entries = ReadChangelogVersions(text);

        Assert.NotEmpty(entries);

        List<string> problems = [];
        for (int index = 1; index < entries.Count; index++)
        {
            if (entries[index] >= entries[index - 1])
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{entries[index - 1]} is followed by v{entries[index]}"));
            }
        }

        Assert.True(
            problems.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SpecificationRelativePath}'s changelog is newest-first, and the other test in this"
                + $" file reads the first entry as the current version — so an entry out of order makes"
                + $" that comparison meaningless rather than merely untidy. Out of order:"
                + $" {string.Join("; ", problems)}."));
    }

    private static Version ReadHeaderVersion(string text)
    {
        int marker = text.IndexOf(HeaderMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new InvalidOperationException(
                $"{SpecificationRelativePath} has no line beginning '{HeaderMarker}', so its stated"
                + " version could not be read. The header is where a reader looks first; if it has"
                + " moved, this test has to move with it rather than be deleted.");
        }

        int start = marker + HeaderMarker.Length;
        int end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        return ParseVersion(text[start..end], "the header");
    }

    private static IReadOnlyList<Version> ReadChangelogVersions(string text)
    {
        int changelog = text.IndexOf(ChangelogHeading, StringComparison.Ordinal);
        if (changelog < 0)
        {
            throw new InvalidOperationException(
                $"{SpecificationRelativePath} has no '{ChangelogHeading}' heading, so its entries could"
                + " not be found.");
        }

        List<Version> versions = [];
        foreach (string line in text[changelog..].Split('\n'))
        {
            if (!line.StartsWith(EntryMarker, StringComparison.Ordinal))
            {
                continue;
            }

            int start = EntryMarker.Length;
            int end = start;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
            {
                end++;
            }

            versions.Add(ParseVersion(line[start..end], "a changelog entry"));
        }

        return versions;
    }

    private static Version ParseVersion(string candidate, string where)
        => Version.TryParse(candidate, out Version? parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"'{candidate}' from {where} of {SpecificationRelativePath} is not a version number.");

    private static string ReadSpecification()
    {
        DirectoryInfo repositoryRoot = FindRepositoryRoot();
        string path = Path.Combine(
            repositoryRoot.FullName,
            SpecificationRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                + " §2 describes.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the end-to-end harness uses, and it fails rather
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
