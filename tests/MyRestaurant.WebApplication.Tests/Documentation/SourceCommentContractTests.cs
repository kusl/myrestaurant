using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Documentation;

public sealed class SourceCommentContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private static readonly string[] SourceRoots = ["src", "tests"];

    private static readonly string[] SourceExtensions = ["*.cs", "*.razor"];

    private static readonly string[] UnauthoredDirectoryNames = ["bin", "obj"];

    private const int MinimumFilesScanned = 250;

    private const int MinimumBytesScanned = 1_500_000;

    private const int ReportedFileLimit = 12;

    private static readonly string[] FixturesThatAreComments =
    [
        "int first = 1; // a trailing line comment\n",
        "/// <summary>A documentation comment.</summary>\nint second = 2;\n",
        "/* a block comment */\nint third = 3;\n",
        "@* a Razor comment *@\n<p>markup</p>\n",
        "int fourth = 4;\n/*\n  a block comment over several lines\n*/\n",
    ];

    private static readonly string[] FixturesThatOnlyLookLikeComments =
    [
        "string address = \"https://example.test/path\";\n",
        "string opener = \"/*\";\n",
        "string marker = \"//\";\n",
        "<a href=\"https://example.test\">a link</a>\n",
    ];

    [Fact]
    public void NoAuthoredSourceFileCarriesAComment()
    {
        int filesScanned = 0;
        long bytesScanned = 0;
        List<string> commented = [];

        foreach (string path in EnumerateSourceFiles())
        {
            string text = File.ReadAllText(path);

            filesScanned++;
            bytesScanned += text.Length;

            string code = SourceCode.WithoutComments(text);

            if (string.Equals(code, text, StringComparison.Ordinal))
            {
                continue;
            }

            commented.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Relative(path)}:{FirstDifferingLine(text, code)}"));
        }

        Assert.True(
            filesScanned >= MinimumFilesScanned,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {filesScanned} authored source file(s) were scanned and this tree has at least"
                + $" {MinimumFilesScanned}, so this fact is not reading the repository it is about. Like"
                + $" every emptiness assertion here the correct answer is zero, which is also what a walk"
                + $" that opened nothing returns (F-41)."));

        Assert.True(
            bytesScanned >= MinimumBytesScanned,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {bytesScanned} byte(s) were read across {filesScanned} file(s) and at least"
                + $" {MinimumBytesScanned} exist. Distinct from the file count and not redundant with it:"
                + $" a reader handed empty strings would open every file, find no comment, and satisfy"
                + $" the count above."));

        Assert.True(
            commented.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{commented.Count} authored source file(s) carry a comment:"
                + $" {FormatList(commented)}. This tree states its reasoning in documents and in git,"
                + $" not beside the code, because a comment is the one claim about a program that"
                + $" nothing can check — F-77's ruling arriving as a rule rather than as a judgement on"
                + $" one paragraph. Name the thing so it needs no gloss; if the reasoning is worth"
                + $" keeping, it belongs in TECHNICAL_SPECIFICATION.md, in DOCUMENTATION_REVIEW.md, or"
                + $" in the commit message. The line number is where the file and its comment-free form"
                + $" first diverge. One residual is worth knowing before reaching for a suppression:"
                + $" SourceCode.WithoutComments reads a multi-line verbatim or raw string literal as"
                + $" code on its inner lines, so a '//' inside multi-line SQL is reported here even"
                + $" though the compiler sees a literal. No such literal is in this tree; if one"
                + $" arrives, the repair is that reader rather than this rule."));
    }

    [Fact]
    public void TheScanFindsEveryCommentFormAndReportsNoneOfTheThingsThatMerelyLookLikeOne()
    {
        Assert.All(
            FixturesThatAreComments,
            fixture => Assert.NotEqual(fixture, SourceCode.WithoutComments(fixture)));

        Assert.All(
            FixturesThatOnlyLookLikeComments,
            fixture => Assert.Equal(fixture, SourceCode.WithoutComments(fixture)));
    }

    private static int FirstDifferingLine(string text, string code)
    {
        string[] left = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] right = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (int index = 0; index < left.Length && index < right.Length; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return Math.Min(left.Length, right.Length) + 1;
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (string root in SourceRoots)
        {
            foreach (string pattern in SourceExtensions)
            {
                foreach (string path in Directory.EnumerateFiles(
                    PathTo(root), pattern, SearchOption.AllDirectories))
                {
                    if (IsUnauthored(path))
                    {
                        continue;
                    }

                    yield return path;
                }
            }
        }
    }

    private static bool IsUnauthored(string path)
    {
        foreach (string segment in
            Relative(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (UnauthoredDirectoryNames.Contains(segment, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Relative(string absolutePath)
        => Path.GetRelativePath(FindRepositoryRoot().FullName, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string FormatList(IEnumerable<string> values)
    {
        List<string> all = [.. values];

        string joined = all.Count <= ReportedFileLimit
            ? string.Join("; ", all)
            : string.Join("; ", all.Take(ReportedFileLimit))
                + string.Create(
                    CultureInfo.InvariantCulture,
                    $"; and {all.Count - ReportedFileLimit} more");

        return joined.Length == 0 ? "(none)" : joined;
    }

    private static string PathTo(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                    + " §2 describes.");
        }

        return path;
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
