using System.Text;

namespace MyRestaurant.WebApplication.Tests;

internal static class SourceCode
{
    public static string WithoutComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder code = new(text.Length);

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
