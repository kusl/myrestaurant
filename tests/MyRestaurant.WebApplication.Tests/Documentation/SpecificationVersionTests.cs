using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

/// <summary>
/// Every versioned document in <c>docs/</c> says the same version at both ends of itself
/// (TECHNICAL_SPECIFICATION §16.4, §18).
///
/// <para><b>Why this is a test.</b> It had drifted twice before there was one. The v1.3 changelog entry
/// carries the first correction in its own words — <em>"the header of this document read v1.1 while the
/// changelog below already carried a v1.2 entry — Slice 16 bumped one and not the other"</em> — and Slice
/// 22 did the identical thing, shipping a v1.7 entry under a header reading v1.6. That was F-48.</para>
///
/// <para><b>Why it no longer names a document.</b> Because it drifted a <em>third</em> time, and this test
/// was standing next to it. F-48's fix pinned <c>docs/TECHNICAL_SPECIFICATION.md</c> by name in a
/// <c>const string</c>, and from Slice 24 until Slice 30 <c>docs/REQUIREMENTS.md</c> sat in the tree
/// saying <em>"Revision 4"</em> in its header above a revision history whose newest entry was
/// <em>"Rev 5"</em> — the same defect, in the sibling document, four rows below a gate built to make it
/// unrepeatable. That is F-58, and it is F-46's lesson arriving one register lower again: <b>a rule
/// enforced against a list of examples is enforced against a list of examples</b>, and a list of one is
/// still a list. So the subject is now <em>computed</em>: every Markdown file in <c>docs/</c> that has both
/// a version in its header and a history section is checked, and nothing anywhere names which files those
/// are (F-47's habit — where the rule can be executed, the list should not exist).
/// </para>
///
/// <para><b>Why the vocabularies are read together rather than tabled per document.</b> The specification
/// says <c>**Version 1.15</c> over a <c>## Changelog</c> of <c>**v1.15 — …</c> entries; the requirements
/// say <c>**Revision 6</c> over a <c>## Revision history</c> of <c>- **Rev 6 — …</c> entries. A lookup
/// table keyed by filename would be the list this test just stopped keeping, so both spellings are
/// admitted by one pattern instead. A document that invents a third spelling is not silently skipped:
/// a header version with no readable history, or the reverse, is reported as a finding rather than
/// passed over.</para>
///
/// <para><b>What it still refuses to check.</b> Dates, section numbers, whether an entry exists for the
/// current commit, and whether the prose is any good. Those are judgements about content; this is
/// arithmetic about one string per document, and a gate that reaches past what it can decide reports
/// findings on correct trees (F-41). It reports what it skipped, for the same reason (F-41 again).</para>
/// </summary>
public sealed class SpecificationVersionTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string DocumentsRelativePath = "docs";

    /// <summary>
    /// How many documents must turn out to be versioned. Two are: the specification and the
    /// requirements. Without this the whole class passes on a tree where the header markers were
    /// reworded and nothing is examined — which is the failure mode of every scan that computes its own
    /// subject.
    /// </summary>
    private const int MinimumVersionedDocuments = 2;

    /// <summary>
    /// A header's stated version: <c>**Version 1.15 — …</c> or <c>**Revision 6 — …</c>. Anchored to the
    /// double asterisk so a mention in prose is not mistaken for the statement.
    /// </summary>
    private static readonly Regex HeaderVersion = new(@"\*\*(?:Version|Revision)\s+(\d+(?:\.\d+)*)");

    /// <summary>Where the entries start. Nothing above the match is parsed as one.</summary>
    private static readonly Regex HistoryHeading =
        new(@"^##\s+(?:Changelog|Revision history)\s*$", RegexOptions.Multiline);

    /// <summary>
    /// An entry at the start of a line, bulleted or not: <c>**v1.8 — …</c>, <c>- **Rev 6 — …</c>.
    /// </summary>
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

    /// <summary>
    /// The set is derived from the documents themselves. A file with a header version and no readable
    /// history — or a history with no header version — is a finding rather than a skip: those are the two
    /// shapes in which a document could quietly leave this test's subject.
    /// </summary>
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

    /// <summary>
    /// A one-component version — the requirements' <c>Revision 6</c> — is padded so
    /// <see cref="Version"/> will take it. <c>Version.TryParse("6")</c> fails; <c>"6.0"</c> does not,
    /// and comparison against another padded revision is the same comparison.
    /// </summary>
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

    /// <summary>One document's two ends: what its header claims, and what its history records.</summary>
    private sealed record VersionedDocument(string Name, Version Header, IReadOnlyList<Version> Entries);
}
