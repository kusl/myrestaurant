using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class SourceCodeTests
{
    private const string Marker = "Marker";

    [Fact]
    public void ADocumentationCommentIsProseHoweverExactlyItQuotesItsSubject()
    {
        string source = Lines(
            "    /// The page opts in with <c>[" + Marker + "(…)]</c> naming this value.",
            "    public const string PolicyName = \"display-pairing\";");

        string code = SourceCode.WithoutComments(source);

        Assert.DoesNotContain(Marker, code, StringComparison.Ordinal);
        Assert.Contains("public const string PolicyName", code, StringComparison.Ordinal);

        Assert.Equal(CountNewlines(source), CountNewlines(code));
    }

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

        string spanning = Lines(
            "int first = 1; /* " + Marker,
            "   still inside " + Marker + " */ int second = 2;");

        string afterBlockComment = SourceCode.WithoutComments(spanning);

        Assert.DoesNotContain(Marker, afterBlockComment, StringComparison.Ordinal);
        Assert.Contains("int first = 1;", afterBlockComment, StringComparison.Ordinal);
        Assert.Contains("int second = 2;", afterBlockComment, StringComparison.Ordinal);

        string scopedStyle = Lines(
            "<style>",
            "  /* " + Marker + " */",
            "  .record-title { font-weight: 600; }",
            "</style>");

        string afterStyle = SourceCode.WithoutComments(scopedStyle);

        Assert.DoesNotContain(Marker, afterStyle, StringComparison.Ordinal);
        Assert.Contains(".record-title", afterStyle, StringComparison.Ordinal);

        string unclosed = Lines(
            "int first = 1; /* " + Marker,
            "int second = 2;");

        string afterUnclosed = SourceCode.WithoutComments(unclosed);

        Assert.DoesNotContain(Marker, afterUnclosed, StringComparison.Ordinal);
        Assert.DoesNotContain("int second", afterUnclosed, StringComparison.Ordinal);
    }

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

    [Fact]
    public void AStringLiteralIsCodeEvenWhenItLooksLikeAComment()
    {
        string source = Lines(
            "<a href=\"https://example.test/" + Marker + "\">source</a>",
            "string opener = \"/* " + Marker + "\";",
            "string quoted = \"a \\\" and then " + Marker + "\";",
            "int last = 3;");

        string code = SourceCode.WithoutComments(source);

        Assert.Equal(3, CountOccurrences(code, Marker));
        Assert.Contains("int last = 3;", code, StringComparison.Ordinal);
        Assert.Equal(CountNewlines(source), CountNewlines(code));
    }

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
