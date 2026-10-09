using System.Buffers;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.Text;

/// <summary>One part of a Markdown document: the text from a heading to the next one, headings included.</summary>
/// <param name="Heading">The heading trail (<see cref="HeadingTrail"/>), or <see langword="null"/> before the first heading.</param>
/// <param name="FirstLine">The line, counted from 1, the section starts on.</param>
/// <param name="Text">The Markdown of the section as it is written.</param>
internal sealed record MarkdownSection(string? Heading, int FirstLine, string Text);

/// <summary>
/// Cuts Markdown into the parts its headings make, so an answer can say which part it came from. It knows the parts of the
/// language that decide what a heading is, and no more: a <c>#</c> line inside a code fence is code, a line that needs a space
/// after its <c>#</c> (<c>#tag</c> is not a heading), a <c>===</c> or <c>---</c> line under a line of text makes it a heading,
/// the same line after a blank line is a rule, and a block of YAML front matter at the start is not headings. The text is kept
/// as it is written: Markdown is readable and its markers (<c>#</c>, <c>-</c>, <c>|</c>) tell a model what the text is.
/// </summary>
internal static class MarkdownSectionSplitter
{
    /// <summary>A line longer than this is text, never a heading.</summary>
    private const int MaxHeadingLineLength = 2_000;

    private const int MaxFrontMatterLines = 200;

    private static readonly SearchValues<char> RuleCharacters = SearchValues.Create("-*_= \t");

    /// <summary>
    /// Splits <paramref name="text"/> (line breaks already <c>\n</c>). A document with no heading is one section with no heading.
    /// </summary>
    public static IReadOnlyList<MarkdownSection> Split(string text)
    {
        var lines = text.Split('\n');
        var headings = FindHeadings(lines);

        var sections = new List<MarkdownSection>();
        var trail = new HeadingTrail();

        if (headings.Count == 0 || headings[0].Line > 0)
        {
            var end = headings.Count == 0 ? lines.Length : headings[0].Line;
            sections.Add(new MarkdownSection(null, 1, string.Join('\n', lines[..end])));
        }

        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Line;
            var end = i + 1 < headings.Count ? headings[i + 1].Line : lines.Length;
            sections.Add(new MarkdownSection(
                trail.Enter(headings[i].Level, headings[i].Text), start + 1, string.Join('\n', lines[start..end])));
        }

        return sections;
    }

    /// <summary>True when <paramref name="text"/> has a heading that <see cref="Split"/> would cut at.</summary>
    public static bool HasHeadings(string text) => FindHeadings(text.Split('\n')).Count > 0;

    private static List<(int Line, int Level, string Text)> FindHeadings(string[] lines)
    {
        var headings = new List<(int Line, int Level, string Text)>();
        var i = SkipFrontMatter(lines);
        var fenceChar = '\0';
        var fenceLength = 0;
        var lastHeadingLine = -1;

        for (; i < lines.Length; i++)
        {
            var line = lines[i];

            if (fenceChar != '\0')
            {
                if (IsFenceClose(line, fenceChar, fenceLength))
                {
                    fenceChar = '\0';
                }

                continue;
            }

            if (TryFenceOpen(line, out fenceChar, out fenceLength))
            {
                continue;
            }

            if (line.Length <= MaxHeadingLineLength && TryAtxHeading(line, out var level, out var atxText))
            {
                headings.Add((i, level, atxText));
                lastHeadingLine = i;
                continue;
            }

            // Text under a line of = or - : the text is the heading. It needs a line of text right above, and a blank line above a
            // --- makes it a rule instead.
            if (i > 0 && i - 1 > lastHeadingLine && line.Length <= MaxHeadingLineLength && IsSetextUnderline(line, out var underline)
                && IsParagraphLine(lines[i - 1]))
            {
                headings.Add((i - 1, underline == '=' ? 1 : 2, lines[i - 1].Trim()));
                lastHeadingLine = i;
            }
        }

        return headings;
    }

    // A block of "---" ... "---" (or "...") at the very start of the file.
    private static int SkipFrontMatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return 0;
        }

        for (var j = 1; j < lines.Length && j <= MaxFrontMatterLines; j++)
        {
            var closing = lines[j].TrimEnd();
            if (closing is "---" or "...")
            {
                return j + 1;
            }
        }

        return 0;
    }

    private static bool TryAtxHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var span = line.AsSpan();
        var indent = 0;
        while (indent < span.Length && indent < 4 && span[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3)
        {
            return false;
        }

        span = span[indent..];
        var hashes = 0;
        while (hashes < span.Length && span[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is 0 or > 6 || (hashes < span.Length && span[hashes] is not (' ' or '\t')))
        {
            return false;
        }

        var rest = span[hashes..].Trim(" \t");

        // A closing run of #s counts only when a space or tab comes before it (or it is all there is).
        var end = rest.Length;
        while (end > 0 && rest[end - 1] == '#')
        {
            end--;
        }

        if (end < rest.Length && (end == 0 || rest[end - 1] is ' ' or '\t'))
        {
            rest = rest[..end].TrimEnd(" \t");
        }

        level = hashes;
        text = rest.ToString();
        return true;
    }

    private static bool IsSetextUnderline(string line, out char marker)
    {
        marker = '\0';
        var span = line.AsSpan();
        var i = 0;
        while (i < span.Length && i < 4 && span[i] == ' ')
        {
            i++;
        }

        if (i > 3 || i >= span.Length || span[i] is not ('=' or '-'))
        {
            return false;
        }

        marker = span[i];
        while (i < span.Length && span[i] == marker)
        {
            i++;
        }

        return span[i..].Trim(" \t").IsEmpty;
    }

    // A line that can be a paragraph's last line: text, not blank, not another kind of block.
    private static bool IsParagraphLine(string line)
    {
        var span = line.AsSpan();
        var indent = 0;
        while (indent < span.Length && span[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || indent >= span.Length || span[indent] is '\t')
        {
            return false;
        }

        var content = span[indent..].TrimEnd(" \t");
        if (content.IsEmpty)
        {
            return false;
        }

        // A rule or another underline (---, ***, ___, ===) is not text.
        if (content.IndexOfAnyExcept(RuleCharacters) < 0)
        {
            return false;
        }

        var first = content[0];
        if (first is '#' or '>' or '`' or '~')
        {
            return false;
        }

        // A list item ("- x", "* x", "+ x", "1. x", "1) x") is not the text of a paragraph.
        if (first is '-' or '*' or '+')
        {
            return content.Length == 1 || content[1] is not (' ' or '\t');
        }

        if (char.IsAsciiDigit(first))
        {
            var d = 1;
            while (d < content.Length && char.IsAsciiDigit(content[d]))
            {
                d++;
            }

            if (d < content.Length && content[d] is '.' or ')' && d + 1 < content.Length && content[d + 1] is ' ' or '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFenceOpen(string line, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;
        var span = line.AsSpan();
        var indent = 0;
        while (indent < span.Length && indent < 4 && span[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || indent >= span.Length || span[indent] is not ('`' or '~'))
        {
            return false;
        }

        var marker = span[indent];
        var count = 0;
        while (indent + count < span.Length && span[indent + count] == marker)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        // The info string of a backtick fence cannot hold a backtick (that would be inline code).
        if (marker == '`' && span[(indent + count)..].Contains('`'))
        {
            return false;
        }

        fenceChar = marker;
        fenceLength = count;
        return true;
    }

    private static bool IsFenceClose(string line, char fenceChar, int fenceLength)
    {
        var span = line.AsSpan();
        var indent = 0;
        while (indent < span.Length && indent < 4 && span[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3)
        {
            return false;
        }

        var count = 0;
        while (indent + count < span.Length && span[indent + count] == fenceChar)
        {
            count++;
        }

        return count >= fenceLength && span[(indent + count)..].Trim(" \t").IsEmpty;
    }
}
