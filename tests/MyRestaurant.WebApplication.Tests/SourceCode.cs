using System.Text;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Source text with its comments removed, so that a gate scanning this tree reads what the compiler
/// reads rather than what somebody wrote <em>about</em> what the compiler reads
/// (TECHNICAL_SPECIFICATION §16.4, <b>F-116</b>).
///
/// <para><b>Why this exists.</b> F-67 ruled that a gate keys on a call rather than on an identifier, and
/// that the open parenthesis is what tells the two apart — a mention of <c>Foo</c> in prose is not
/// <c>Foo(</c>. That ruling is correct about identifiers and it is <em>not</em> correct about a form.
/// <c>RateLimitingContractTests</c> shipped with a scan for <c>EnableRateLimiting\(</c> and a summary
/// asserting the parenthesis made it safe; the very file it was written to protect explains the
/// attribute in a documentation comment that spells the whole form, placeholder argument included, and
/// the gate reported a finding on a correct tree on its first real run. <b>The lesson is not that the
/// prose was written badly.</b> A documentation comment explaining a construct will spell that
/// construct, that is what an explanation is, and any gate whose correctness depends on prose declining
/// to quote its own subject is a gate that will fail again — so the durable answer is a scan that cannot
/// see prose at all.</para>
///
/// <para><b>One decision procedure, not two.</b> Two gates now depend on this: the rate-limiting opt-in
/// scan, and <c>RawHtmlContractTests</c>. Both would otherwise have grown their own idea of what a
/// comment is, and two readers of one rule are two readers that can disagree — which is the shape this
/// project has recorded under F-50, F-56, F-65 and F-107. The behaviour is asserted in
/// <see cref="SourceCodeTests"/> against composed fixtures rather than against the tree, so the proof
/// survives the tree changing.</para>
///
/// <para><b>What it understands, and it is the four forms this tree actually contains.</b> A C# line
/// comment (which covers <c>///</c> without needing to know about it), a C# block comment (which covers
/// the CSS comments inside a component's scoped <c>&lt;style&gt;</c> block, since the delimiters are the
/// same), a Razor comment, and a double-quoted string — the last so that <c>"https://…"</c> is not read
/// as a comment and a <c>"/*"</c> inside a literal cannot open one.</para>
///
/// <para><b>Two properties are deliberate rather than incidental.</b> Line structure is preserved
/// exactly: every newline in the input is a newline in the output, so a caller may still count lines,
/// and — more importantly — a line comment can never swallow the line after it. And a comment that is
/// opened and never closed consumes the rest of the input rather than being ignored, because the
/// compiler would do the same and a scan that disagreed with the compiler about where code is would be
/// reporting on a file that does not build.</para>
///
/// <para><b>The residual, stated rather than closed.</b> This is a lexer for four forms and not a C#
/// parser. A verbatim or raw string literal whose <em>body</em> spans lines is read as code on its inner
/// lines, so a <c>//</c> inside multi-line SQL would cut that line short — which loses code and is
/// therefore the safe direction: every consumer here asserts a floor on what it found, so losing a real
/// site fails loudly rather than passing quietly. A string opened inside an interpolation hole is
/// mis-paired for the remainder of its line, with the same consequence. What cannot happen is the
/// unsafe direction — prose surviving as code — because a comment marker outside a literal always
/// wins.</para>
/// </summary>
internal static class SourceCode
{
    /// <summary>
    /// <paramref name="text"/> with every comment removed and every newline kept.
    /// </summary>
    public static string WithoutComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder code = new(text.Length);

        // Carried across lines, because two of the four forms are allowed to span them.
        bool inBlockComment = false;
        bool inRazorComment = false;

        int lineStart = 0;

        while (lineStart <= text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline;

            AppendCodeOnLine(code, text, lineStart, lineEnd, ref inBlockComment, ref inRazorComment);

            if (newline < 0)
            {
                break;
            }

            code.Append('\n');
            lineStart = newline + 1;
        }

        return code.ToString();
    }

    /// <summary>
    /// The code on one line, appended. The line is bounded so that no state machine below can reach
    /// past a newline by accident, which is what makes the swallow-the-next-line failure impossible
    /// rather than merely unlikely.
    /// </summary>
    private static void AppendCodeOnLine(
        StringBuilder code,
        string text,
        int lineStart,
        int lineEnd,
        ref bool inBlockComment,
        ref bool inRazorComment)
    {
        int index = lineStart;

        while (index < lineEnd)
        {
            if (inBlockComment)
            {
                int close = IndexOfPair(text, index, lineEnd, '*', '/');

                if (close < 0)
                {
                    return;
                }

                inBlockComment = false;
                index = close + 2;
                continue;
            }

            if (inRazorComment)
            {
                int close = IndexOfPair(text, index, lineEnd, '*', '@');

                if (close < 0)
                {
                    return;
                }

                inRazorComment = false;
                index = close + 2;
                continue;
            }

            int segmentStart = index;
            bool emitted = false;
            bool inStringLiteral = false;

            while (index < lineEnd)
            {
                char current = text[index];

                if (inStringLiteral)
                {
                    if (current == '\\')
                    {
                        // An escape consumes the character after it, which is how \" stays inside the
                        // literal. Clamped, so a backslash at the end of a line cannot step over the
                        // newline.
                        index = Math.Min(index + 2, lineEnd);
                        continue;
                    }

                    if (current == '"')
                    {
                        inStringLiteral = false;
                    }

                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inStringLiteral = true;
                    index++;
                    continue;
                }

                char next = index + 1 < lineEnd ? text[index + 1] : '\0';

                if (current == '/' && next == '/')
                {
                    code.Append(text, segmentStart, index - segmentStart);
                    return;
                }

                if (current == '/' && next == '*')
                {
                    code.Append(text, segmentStart, index - segmentStart);
                    emitted = true;
                    inBlockComment = true;
                    index += 2;
                    break;
                }

                if (current == '@' && next == '*')
                {
                    code.Append(text, segmentStart, index - segmentStart);
                    emitted = true;
                    inRazorComment = true;
                    index += 2;
                    break;
                }

                index++;
            }

            if (!emitted)
            {
                code.Append(text, segmentStart, index - segmentStart);
            }
        }
    }

    /// <summary>
    /// Where <paramref name="first"/> is immediately followed by <paramref name="second"/> within the
    /// bounded range, or <c>-1</c>. Bounded rather than open-ended so that a closing delimiter on a
    /// later line is not found from this one.
    /// </summary>
    private static int IndexOfPair(string text, int start, int end, char first, char second)
    {
        for (int index = start; index + 1 < end; index++)
        {
            if (text[index] == first && text[index + 1] == second)
            {
                return index;
            }
        }

        return -1;
    }
}
