using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.UI.Messages;

/// <summary>
/// Reads the emphasis inside a block of an answer's text, written as in Markdown (PROJECT_SPEC §4.2): bold
/// (<c>**text**</c>, <c>__text__</c>), italic (<c>*text*</c>, <c>_text_</c>), both (<c>***text***</c>), strikethrough
/// (<c>~~text~~</c>), inline code (<c>`text`</c>), links (<c>[words](https://…)</c>) and bare web addresses. A backslash
/// shows the mark after it as written.
/// </summary>
/// <remarks>
/// <para>
/// A mark that never closes is text, so a streaming answer shows <c>**Bio</c> as typed until the rest arrives, and
/// then bold. Marks close only where they were opened, at the same length; <c>_</c> never emphasizes inside a word,
/// so <c>snake_case_names</c> stay as they are; a mark next to a space opens nothing, so <c>2 * 3 * 4</c> stays too.
/// Only <c>http</c>, <c>https</c> and <c>mailto</c> addresses become links.
/// </para>
/// </remarks>
public static partial class InlineMarkdown
{
    // Longer text is shown as it is: reading it for marks could take the interface's thread for too long.
    private const int MaxLength = 20_000;

    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>Reads <paramref name="text"/> as runs of one style each, in order. Empty text has none.</summary>
    public static IReadOnlyList<InlineSpan> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var spans = new List<InlineSpan>();
        if (text.Length > MaxLength)
        {
            spans.Add(new InlineSpan(text));
            return spans;
        }

        ParseRange(text, 0, text.Length, InlineStyle.None, null, spans);
        return Merge(spans);
    }

    /// <summary>The words of <paramref name="text"/> without the marks that style them.</summary>
    public static string ToPlainText(string text) => string.Concat(Parse(text).Select(span => span.Text));

    [GeneratedRegex(@"https?://[^\s<>\[\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareAddress();

    private static void ParseRange(string text, int start, int end, InlineStyle style, string? url, List<InlineSpan> output)
    {
        var plain = new StringBuilder();
        var index = start;
        while (index < end)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < end && Punctuation.Contains(text[index + 1], StringComparison.Ordinal))
            {
                plain.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (character == '`')
            {
                var length = RunLength(text, index, end);
                var close = FindBackticks(text, index + length, end, length);
                if (close < 0)
                {
                    plain.Append('`', length);
                    index += length;
                    continue;
                }

                Flush(plain, style, url, output);
                output.Add(new InlineSpan(CodeText(text[(index + length)..close]), style | InlineStyle.Code, url));
                index = close + length;
                continue;
            }

            if (character is '*' or '_' or '~')
            {
                index = ReadEmphasis(text, index, end, style, url, output, plain);
                continue;
            }

            if (character == '[' && url is null && TryLink(text, index, end, style, output, plain, out var next))
            {
                index = next;
                continue;
            }

            plain.Append(character);
            index++;
        }

        Flush(plain, style, url, output);
    }

    // Reads the run of marks at index as an opening mark and, if its closing mark is there, the emphasis between them.
    // Otherwise the run is text. Returns where reading goes on.
    private static int ReadEmphasis(
        string text, int index, int end, InlineStyle style, string? url, List<InlineSpan> output, StringBuilder plain)
    {
        var mark = text[index];
        var length = RunLength(text, index, end);
        var flag = FlagFor(mark, length);
        var close = flag != InlineStyle.None && CanOpen(text, index, length, end, mark)
            ? FindCloser(text, index + length, end, mark, length)
            : -1;
        if (close <= index + length)
        {
            plain.Append(mark, length);
            return index + length;
        }

        Flush(plain, style, url, output);
        ParseRange(text, index + length, close, style | flag, url, output);
        return close + length;
    }

    private static InlineStyle FlagFor(char mark, int length) => mark switch
    {
        '~' => length == 2 ? InlineStyle.Strikethrough : InlineStyle.None,
        _ => length switch
        {
            1 => InlineStyle.Italic,
            2 => InlineStyle.Bold,
            3 => InlineStyle.Bold | InlineStyle.Italic,
            _ => InlineStyle.None,
        },
    };

    // An opening mark is followed by a word, not a space, and an underscore does not begin inside a word.
    private static bool CanOpen(string text, int index, int length, int end, char mark)
    {
        var after = index + length;
        if (after >= end || char.IsWhiteSpace(text[after]))
        {
            return false;
        }

        return mark != '_' || index == 0 || !char.IsLetterOrDigit(text[index - 1]);
    }

    // The start of the run of marks that closes the emphasis opened before start: the same length, after a word rather
    // than a space, and for an underscore not inside a word. Code and escaped marks in between are not marks.
    private static int FindCloser(string text, int start, int end, char mark, int length)
    {
        var index = start;
        while (index < end)
        {
            var character = text[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }

            if (character == '`')
            {
                var ticks = RunLength(text, index, end);
                var close = FindBackticks(text, index + ticks, end, ticks);
                index = close < 0 ? index + ticks : close + ticks;
                continue;
            }

            if (character != mark)
            {
                index++;
                continue;
            }

            var run = RunLength(text, index, end);
            var afterRun = index + run;
            if (run == length && !char.IsWhiteSpace(text[index - 1])
                && (mark != '_' || afterRun >= text.Length || !char.IsLetterOrDigit(text[afterRun])))
            {
                return index;
            }

            index = afterRun;
        }

        return -1;
    }

    // Reads [words](address) at index: the words are read for emphasis too, and the address must be a web or mail one.
    private static bool TryLink(
        string text, int index, int end, InlineStyle style, List<InlineSpan> output, StringBuilder plain, out int next)
    {
        next = index;
        var close = FindBracket(text, index + 1, end);
        if (close < 0 || close + 1 >= end || text[close + 1] != '(' || close == index + 1)
        {
            return false;
        }

        var addressStart = close + 2;
        var addressEnd = FindAddressEnd(text, addressStart, end);
        if (addressEnd < 0)
        {
            return false;
        }

        var address = text[addressStart..addressEnd].Trim();
        var space = address.IndexOfAny([' ', '\t']);
        if (space >= 0)
        {
            address = address[..space];
        }

        address = address.Trim('<', '>');
        if (!IsWebOrMail(address))
        {
            return false;
        }

        Flush(plain, style, null, output);
        ParseRange(text, index + 1, close, style | InlineStyle.Link, address, output);
        next = addressEnd + 1;
        return true;
    }

    private static int FindBracket(string text, int start, int end)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            switch (text[index])
            {
                case '\\':
                    index++;
                    break;
                case '[':
                    depth++;
                    break;
                case ']' when depth == 0:
                    return index;
                case ']':
                    depth--;
                    break;
            }
        }

        return -1;
    }

    // The ")" that ends an address, which may hold balanced parentheses of its own, as Wikipedia's do.
    private static int FindAddressEnd(string text, int start, int end)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            switch (text[index])
            {
                case '\n':
                    return -1;
                case '(':
                    depth++;
                    break;
                case ')' when depth == 0:
                    return index;
                case ')':
                    depth--;
                    break;
            }
        }

        return -1;
    }

    private static bool IsWebOrMail(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto"
        && (uri.Scheme == "mailto" || uri.Host.Length > 0);

    private static int RunLength(string text, int index, int end)
    {
        var length = 1;
        while (index + length < end && text[index + length] == text[index])
        {
            length++;
        }

        return length;
    }

    // The next run of exactly length backticks at or after start, or -1.
    private static int FindBackticks(string text, int start, int end, int length)
    {
        var index = start;
        while (index < end)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var run = RunLength(text, index, end);
            if (run == length)
            {
                return index;
            }

            index += run;
        }

        return -1;
    }

    // Code is shown as written; one space just inside each backtick, which lets code begin or end with one, is not.
    private static string CodeText(string code) =>
        code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim(' ').Length > 0 ? code[1..^1] : code;

    private static void Flush(StringBuilder plain, InlineStyle style, string? url, List<InlineSpan> output)
    {
        if (plain.Length == 0)
        {
            return;
        }

        var text = plain.ToString();
        plain.Clear();
        if (url is not null)
        {
            output.Add(new InlineSpan(text, style, url));
            return;
        }

        // Web addresses that are not written as links are links too.
        var last = 0;
        foreach (Match match in BareAddress().Matches(text))
        {
            var address = TrimAddress(match.Value);
            if (!IsWebOrMail(address))
            {
                continue;
            }

            if (match.Index > last)
            {
                output.Add(new InlineSpan(text[last..match.Index], style));
            }

            output.Add(new InlineSpan(address, style | InlineStyle.Link, address));
            last = match.Index + address.Length;
        }

        if (last < text.Length)
        {
            output.Add(new InlineSpan(text[last..], style));
        }
    }

    // Punctuation that ends a sentence, and a bracket the address did not open, are not part of the address.
    private static string TrimAddress(string address)
    {
        var length = address.Length;
        while (length > 0)
        {
            var last = address[length - 1];
            var opens = address.AsSpan(0, length).Count('(');
            var closes = address.AsSpan(0, length).Count(')');
            if (".,;:!?'\"*_~".Contains(last, StringComparison.Ordinal) || (last == ')' && closes > opens))
            {
                length--;
            }
            else
            {
                break;
            }
        }

        return address[..length];
    }

    private static List<InlineSpan> Merge(List<InlineSpan> spans)
    {
        var merged = new List<InlineSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Style == span.Style && merged[^1].Url == span.Url)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }
}
