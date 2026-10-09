using System.Net;
using System.Text;

namespace Assistant.Documents.Web;

/// <summary>
/// Turns the HTML of a web page into the text a person reads on it, as Markdown-like text: headings as <c>#</c> lines, list items
/// as <c>-</c> lines, paragraphs apart, table cells separated by <c>|</c>. Nothing in the page is run or fetched: scripts, styles,
/// frames, objects and the page's head are skipped without being read, tags are never interpreted beyond what they say about the
/// text's shape, and entities are decoded. It is a reader of text, not a parser of HTML: a broken page gives the text that can
/// be told from it.
/// </summary>
internal static class HtmlTextExtractor
{
    // The longest a single tag is read for its end before it is taken as text.
    private const int MaxTagLength = 64 * 1024;

    // Elements whose content is not the page's text, skipped to their end.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "math", "iframe", "object", "embed", "canvas", "select", "datalist",
    };

    // Elements that start and end a block of text.
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "ul", "ol", "dl", "table", "thead", "tbody",
        "tfoot", "blockquote", "figure", "figcaption", "form", "fieldset", "address", "details", "summary", "center", "body",
    };

    /// <summary>
    /// The text of <paramref name="html"/>, and its title when it has one. At most about <paramref name="maxCharacters"/> of text is
    /// made; <paramref name="truncated"/> says whether the page had more.
    /// </summary>
    public static string Extract(string html, int maxCharacters, CancellationToken cancellationToken, out string? title, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(html);
        var output = new Output(maxCharacters);
        title = null;
        var preDepth = 0;
        var i = 0;
        while (i < html.Length && !output.Full)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var open = html.IndexOf('<', i);
            if (open < 0)
            {
                output.Text(html.AsSpan(i), preDepth > 0);
                i = html.Length;
                break;
            }

            if (open > i)
            {
                output.Text(html.AsSpan(i, open - i), preDepth > 0);
            }

            if (string.CompareOrdinal(html, open, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (open + 1 < html.Length && html[open + 1] is '!' or '?')
            {
                var end = html.IndexOf('>', open + 1);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            if (!TryReadTag(html, open, out var name, out var closing, out var selfClosing, out var tagEnd))
            {
                // A "<" that is not a tag, such as "a < b".
                output.Text("<", false);
                i = open + 1;
                continue;
            }

            i = tagEnd;
            if (closing)
            {
                if (name.Equals("pre", StringComparison.OrdinalIgnoreCase))
                {
                    preDepth = Math.Max(0, preDepth - 1);
                    output.BlankLine();
                }
                else if (name.Length == 2 && name[0] is 'h' or 'H' && name[1] is >= '1' and <= '6')
                {
                    output.BlankLine();
                }
                else if (Blocks.Contains(name) || name.Equals("li", StringComparison.OrdinalIgnoreCase) || name.Equals("tr", StringComparison.OrdinalIgnoreCase))
                {
                    output.NewLine(blank: Blocks.Contains(name) && !name.Equals("ul", StringComparison.OrdinalIgnoreCase) && !name.Equals("ol", StringComparison.OrdinalIgnoreCase));
                }

                continue;
            }

            if (name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                var end = IndexOfClose(html, "title", i);
                if (title is null)
                {
                    title = Tidy(WebUtility.HtmlDecode(html.Substring(i, end - i)));
                }

                i = SkipPast(html, end);
                continue;
            }

            if (Skipped.Contains(name))
            {
                if (!selfClosing)
                {
                    i = SkipPast(html, IndexOfClose(html, name, i));
                }

                continue;
            }

            if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                output.NewLine(blank: false);
            }
            else if (name.Equals("hr", StringComparison.OrdinalIgnoreCase))
            {
                output.BlankLine();
            }
            else if (name.Equals("pre", StringComparison.OrdinalIgnoreCase))
            {
                output.BlankLine();
                preDepth++;
            }
            else if (name.Length == 2 && name[0] is 'h' or 'H' && name[1] is >= '1' and <= '6')
            {
                output.BlankLine();
                output.Raw(new string('#', name[1] - '0') + " ");
            }
            else if (name.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                output.NewLine(blank: false);
                output.Raw("- ");
            }
            else if (name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                output.NewLine(blank: false);
            }
            else if (name.Equals("td", StringComparison.OrdinalIgnoreCase) || name.Equals("th", StringComparison.OrdinalIgnoreCase))
            {
                output.Cell();
            }
            else if (Blocks.Contains(name))
            {
                output.BlankLine();
            }
        }

        truncated = output.Full && i < html.Length;
        return output.ToString();
    }

    // Reads the tag that starts at html[open] == '<': its name, whether it closes (</x>) or closes itself (<x/>), and where it ends.
    private static bool TryReadTag(string html, int open, out string name, out bool closing, out bool selfClosing, out int end)
    {
        name = "";
        closing = selfClosing = false;
        end = open + 1;
        var at = open + 1;
        if (at < html.Length && html[at] == '/')
        {
            closing = true;
            at++;
        }

        var start = at;
        while (at < html.Length && (char.IsAsciiLetterOrDigit(html[at]) || html[at] is ':' or '-'))
        {
            at++;
        }

        if (at == start || !char.IsAsciiLetter(html[start]))
        {
            return false;
        }

        name = html[start..at];

        // To the end of the tag: a ">" inside a quoted attribute value does not end it.
        var quote = '\0';
        var limit = Math.Min(html.Length, at + MaxTagLength);
        for (; at < limit; at++)
        {
            var c = html[at];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                selfClosing = at > 0 && html[at - 1] == '/';
                end = at + 1;
                return true;
            }
        }

        return false;
    }

    // Where the element <name> that begins at html[from] ends: the start of its close tag, or the end of the page.
    private static int IndexOfClose(string html, string name, int from)
    {
        var close = "</" + name;
        var at = from;
        while (true)
        {
            at = html.IndexOf(close, at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return html.Length;
            }

            var after = at + close.Length;
            if (after >= html.Length || !char.IsAsciiLetterOrDigit(html[after]))
            {
                return at;
            }

            at = after;
        }
    }

    // The index just after the tag at html[at] (a close tag), or the end of the page.
    private static int SkipPast(string html, int at)
    {
        if (at >= html.Length)
        {
            return html.Length;
        }

        var end = html.IndexOf('>', at);
        return end < 0 ? html.Length : end + 1;
    }

    private static string? Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    // The text made so far, with the rules of where lines and blank lines may be.
    private sealed class Output(int maxCharacters)
    {
        private readonly StringBuilder _text = new();
        private bool _pendingSpace;

        public bool Full => _text.Length >= maxCharacters;

        // Words of the page: whitespace runs are one space unless the text is preformatted.
        public void Text(ReadOnlySpan<char> run, bool preformatted)
        {
            var decoded = WebUtility.HtmlDecode(run.ToString());
            if (preformatted)
            {
                _text.Append(decoded.Replace("\r\n", "\n", StringComparison.Ordinal));
                _pendingSpace = false;
                return;
            }

            foreach (var c in decoded)
            {
                if (char.IsWhiteSpace(c) || c == ' ')
                {
                    _pendingSpace = _text.Length > 0 && _text[^1] != '\n';
                    continue;
                }

                if (_pendingSpace)
                {
                    _text.Append(' ');
                    _pendingSpace = false;
                }

                _text.Append(c);
            }
        }

        public void Raw(string text)
        {
            _text.Append(text);
            _pendingSpace = false;
        }

        public void Cell()
        {
            if (_text.Length > 0 && _text[^1] != '\n')
            {
                _text.Append(" | ");
            }

            _pendingSpace = false;
        }

        public void NewLine(bool blank)
        {
            TrimLineEnd();
            if (_text.Length == 0)
            {
                return;
            }

            if (_text[^1] != '\n')
            {
                _text.Append('\n');
            }

            if (blank && !EndsWithBlankLine())
            {
                _text.Append('\n');
            }

            _pendingSpace = false;
        }

        public void BlankLine() => NewLine(blank: true);

        public override string ToString() => _text.ToString().TrimEnd();

        private void TrimLineEnd()
        {
            while (_text.Length > 0 && _text[^1] is ' ' or '\t')
            {
                _text.Length--;
            }
        }

        private bool EndsWithBlankLine() => _text.Length >= 2 && _text[^1] == '\n' && _text[^2] == '\n';
    }
}
