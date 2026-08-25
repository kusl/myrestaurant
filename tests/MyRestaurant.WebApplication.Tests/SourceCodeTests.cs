using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// <see cref="SourceCode"/> tells code from prose (TECHNICAL_SPECIFICATION §16.4, <b>F-116</b>).
///
/// <para><b>Why the fixtures are composed rather than the tree read.</b> Two gates depend on this
/// helper, and both of them assert that something is <em>absent</em> from the code — an unregistered
/// policy name, a raw-HTML site nobody recorded. An emptiness assertion cannot demonstrate that the
/// reader it depends on can see anything at all, which is F-41's shape and the reason F-64, F-67 and
/// F-68 each began as an assertion that was true and could not have detected its own subject. So the
/// demonstration lives here, on inputs written for it, and it stays true after the tree changes.</para>
///
/// <para><b>Every fixture is built through <see cref="Lines"/> and the marker is a word of no
/// significance</b>, which is F-114's habit and applied for its reason rather than by imitation: written
/// out as literals, these fixtures would put comment markers at the start of lines in <em>this</em> file,
/// and <c>DocumentationCommentContractTests</c> walks every <c>.cs</c> file under <c>tests/</c> as text.
/// A gate that reports a finding on the file proving another gate works is the same mistake one register
/// up.</para>
///
/// <para><b>The first fact is F-116 itself</b>, reduced to its shape: a documentation comment that spells
/// the construct it is explaining. That is not a mistake anybody made — it is what an explanation of a
/// construct looks like — and it is why the remedy had to be a reader rather than a reworded
/// sentence.</para>
///
/// <para>Pure. No file is opened, no server started, nothing else is touched.</para>
/// </summary>
public sealed class SourceCodeTests
{
    /// <summary>
    /// The token the fixtures hide and the assertions look for. Deliberately meaningless: a fixture
    /// naming a real subject invites a reader to check it against the tree, and the tree is not what is
    /// under test here.
    /// </summary>
    private const string Marker = "Marker";

    /// <summary>
    /// A documentation comment is prose however exactly it quotes the code it describes — which is the
    /// defect, restated as the property.
    /// </summary>
    [Fact]
    public void ADocumentationCommentIsProseHoweverExactlyItQuotesItsSubject()
    {
        // The F-116 shape: the comment spells the whole form, argument placeholder included.
        string source = Lines(
            "    /// The page opts in with <c>[" + Marker + "(…)]</c> naming this value.",
            "    public const string PolicyName = \"display-pairing\";");

        string code = SourceCode.WithoutComments(source);

        Assert.DoesNotContain(Marker, code, StringComparison.Ordinal);
        Assert.Contains("public const string PolicyName", code, StringComparison.Ordinal);

        // Every newline survives, which is what stops a comment on one line reaching the next.
        Assert.Equal(CountNewlines(source), CountNewlines(code));
    }

    /// <summary>
    /// A line comment ends at its line; a block comment does not, and an unclosed one runs to the end of
    /// the input the way the compiler would take it.
    /// </summary>
    [Fact]
    public void ALineCommentEndsAtItsLineAndABlockCommentSpansThem()
    {
        string trailing = Lines(
            "int first = 1; // " + Marker,
            "int second = 2;");

        string afterLineComment = SourceCode.WithoutComments(trailing);

        Assert.DoesNotContain(Marker, afterLineComment, StringComparison.Ordinal);
        Assert.Contains("int first = 1;", afterLineComment, StringComparison.Ordinal);
        Assert.Contains("int second = 2;", afterLineComment, StringComparison.Ordinal);

        // Both lines of the block, and the code sharing the closing line with it.
        string spanning = Lines(
            "int first = 1; /* " + Marker,
            "   still inside " + Marker + " */ int second = 2;");

        string afterBlockComment = SourceCode.WithoutComments(spanning);

        Assert.DoesNotContain(Marker, afterBlockComment, StringComparison.Ordinal);
        Assert.Contains("int first = 1;", afterBlockComment, StringComparison.Ordinal);
        Assert.Contains("int second = 2;", afterBlockComment, StringComparison.Ordinal);

        // The same delimiters are CSS's, which is how a component's scoped <style> block is covered
        // without this helper knowing that CSS exists.
        string scopedStyle = Lines(
            "<style>",
            "  /* " + Marker + " */",
            "  .record-title { font-weight: 600; }",
            "</style>");

        string afterStyle = SourceCode.WithoutComments(scopedStyle);

        Assert.DoesNotContain(Marker, afterStyle, StringComparison.Ordinal);
        Assert.Contains(".record-title", afterStyle, StringComparison.Ordinal);

        // Opened and never closed: the rest is comment, because that is what it would compile as.
        string unclosed = Lines(
            "int first = 1; /* " + Marker,
            "int second = 2;");

        string afterUnclosed = SourceCode.WithoutComments(unclosed);

        Assert.DoesNotContain(Marker, afterUnclosed, StringComparison.Ordinal);
        Assert.DoesNotContain("int second", afterUnclosed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Razor comment is removed across every line it spans, including the lines in the middle of it
    /// that carry no delimiter of their own — which is the form this tree writes its long explanatory
    /// blocks in.
    /// </summary>
    [Fact]
    public void ARazorCommentIsRemovedAcrossEveryLineItSpans()
    {
        string source = Lines(
            "@page \"/register\"",
            "",
            "@* " + Marker,
            "",
            "   a middle line carrying no delimiter and naming " + Marker + " again",
            "*@",
            "<p>@Person.DisplayName</p>");

        string code = SourceCode.WithoutComments(source);

        Assert.DoesNotContain(Marker, code, StringComparison.Ordinal);
        Assert.Contains("@page \"/register\"", code, StringComparison.Ordinal);
        Assert.Contains("<p>@Person.DisplayName</p>", code, StringComparison.Ordinal);
        Assert.Equal(CountNewlines(source), CountNewlines(code));
    }

    /// <summary>
    /// A string literal is code even when it reads like a comment. The first case is the one this tree
    /// actually contains in quantity — a URL in markup — and the second is the one that would be
    /// dangerous, because a block comment opened inside a literal would consume every line after it.
    /// </summary>
    [Fact]
    public void AStringLiteralIsCodeEvenWhenItLooksLikeAComment()
    {
        string source = Lines(
            "<a href=\"https://example.test/" + Marker + "\">source</a>",
            "string opener = \"/* " + Marker + "\";",
            "string quoted = \"a \\\" and then " + Marker + "\";",
            "int last = 3;");

        string code = SourceCode.WithoutComments(source);

        // Three occurrences went in and three came out: nothing inside a literal was read as prose.
        Assert.Equal(3, CountOccurrences(code, Marker));
        Assert.Contains("int last = 3;", code, StringComparison.Ordinal);
        Assert.Equal(CountNewlines(source), CountNewlines(code));
    }

    /// <summary>
    /// Fixture lines joined into a file, so that no comment marker is ever written at the start of a
    /// line in this file.
    /// </summary>
    private static string Lines(params string[] lines)
        => string.Concat(lines.Select(line => line + "\n"));

    private static int CountNewlines(string text)
        => CountOccurrences(text, "\n");

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
}
