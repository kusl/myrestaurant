using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

public sealed class SpecificationVersionTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string DocumentsRelativePath = "docs";

    private const int MinimumVersionedDocuments = 2;

    private static readonly Regex HeaderVersion = new(@"\*\*(?:Version|Revision)\s+(\d+(?:\.\d+)*)");

    private static readonly Regex HistoryHeading =
        new(@"^##\s+(?:Changelog|Revision history)\s*$", RegexOptions.Multiline);

    private static readonly Regex HistoryEntry =
        new(@"^(?:-\s+)?\*\*(?:v|Rev(?:ision)?\s+)(\d+(?:\.\d+)*)", RegexOptions.Multiline);

    [Fact]
    public void EveryVersionedDocumentsHeaderMatchesItsNewestHistoryEntry()
    {
        IReadOnlyList<VersionedDocument> documents = ReadVersionedDocuments();
        List<string> problems = [];

        foreach (VersionedDocument document in documents)
        {
            if (document.Header != document.Entries[0])
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{document.Name} says {document.Header} in its header and {document.Entries[0]} in"
                        + $" its newest history entry"));
            }
        }

        Assert.True(
            problems.Count == 0,
            "One of the two was bumped and the other was not. This has now happened three times — the"
                + " specification twice (see its v1.3 and v1.8 entries) and the requirements once,"
                + " unnoticed for six slices because the gate that exists to prevent it named one file"
                + $" (F-48, F-58). Whichever is right, both have to say it. {FormatList(problems)}.");
    }

    [Fact]
    public void EveryVersionedDocumentsHistoryEntriesDescend()
    {
        IReadOnlyList<VersionedDocument> documents = ReadVersionedDocuments();
        List<string> problems = [];

        foreach (VersionedDocument document in documents)
        {
            for (int index = 1; index < document.Entries.Count; index++)
            {
                if (document.Entries[index] >= document.Entries[index - 1])
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{document.Name}: {document.Entries[index - 1]} is followed by"
                            + $" {document.Entries[index]}"));
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "A history is newest-first, and the other test in this file reads the first entry as the"
                + " current version — so an entry out of order makes that comparison meaningless rather"
                + $" than merely untidy. Out of order: {FormatList(problems)}.");
    }

    private static IReadOnlyList<VersionedDocument> ReadVersionedDocuments()
    {
        List<VersionedDocument> documents = [];
        List<string> halfVersioned = [];
        int skipped = 0;

        foreach (string path in EnumerateDocuments())
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);

            Match header = HeaderVersion.Match(text);
            Match heading = HistoryHeading.Match(text);

            if (!header.Success && !heading.Success)
            {
                skipped++;
                continue;
            }

            if (!header.Success || !heading.Success)
            {
                halfVersioned.Add(header.Success
                    ? $"{name} states a version and has no history section"
                    : $"{name} has a history section and states no version");
                continue;
            }

            List<Version> entries = [];
            foreach (Match entry in HistoryEntry.Matches(text[heading.Index..]))
            {
                entries.Add(ParseVersion(entry.Groups[1].Value, $"a history entry of {name}"));
            }

            if (entries.Count == 0)
            {
                halfVersioned.Add($"{name} has a history section with no readable entries");
                continue;
            }

            documents.Add(new VersionedDocument(
                name,
                ParseVersion(header.Groups[1].Value, $"the header of {name}"),
                entries));
        }

        Assert.True(
            halfVersioned.Count == 0,
            "A document that is half-versioned has left this test's subject without saying so, which is"
                + $" exactly how F-58 stayed green: {FormatList(halfVersioned)}.");

        Assert.True(
            documents.Count >= MinimumVersionedDocuments,
            $"Only {documents.Count} versioned document(s) found in {DocumentsRelativePath}/ and at least"
                + $" {MinimumVersionedDocuments} are expected ({skipped} unversioned file(s) skipped). A"
                + " scan that computes its own subject has to say when the subject came back empty.");

        return documents;
    }

    private static IEnumerable<string> EnumerateDocuments()
        => Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot().FullName, DocumentsRelativePath),
                "*.md",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal);

    private static Version ParseVersion(string candidate, string where)
        => Version.TryParse(
            candidate.Contains('.', StringComparison.Ordinal) ? candidate : candidate + ".0",
            out Version? parsed)
            ? parsed
            : throw new InvalidOperationException($"'{candidate}' from {where} is not a version number.");

    private static string FormatList(IEnumerable<string> values)
    {
        string joined = string.Join("; ", values);
        return joined.Length == 0 ? "(none)" : joined;
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

    private sealed record VersionedDocument(string Name, Version Header, IReadOnlyList<Version> Entries);
}
