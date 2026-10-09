using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.UI.Messages;

/// <summary>
/// Reads an assistant message's text as blocks: paragraphs, headings, lists and code, written as in Markdown. It reads any
/// prefix of a message too, so a streaming answer can be shown as it arrives.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Blank lines separate paragraphs. A single line break stays a line break, since answers use them to lay out
/// short lines.</item>
/// <item><c>#</c> to <c>######</c> followed by a space start a heading.</item>
/// <item><c>-</c>, <c>*</c>, <c>+</c> or <c>•</c> followed by a space start a bulleted item, and a number followed by
/// <c>.</c> or <c>)</c> and a space a numbered one. Numbering starts from the first item's number, as in Markdown,
/// and a numbered item can interrupt a paragraph only when it is 1. Indenting an item further nests it, and an
/// indented line that is not an item continues the item above. A line that is not indented ends the list.</item>
/// </list>
/// <para>
/// Three backticks (with an optional language after them) on a line of their own open a block of code, which a line of
/// three or more backticks closes; without one, the code runs to the end, so a streaming answer's block grows as it
/// arrives. Nothing inside a block is read as Markdown.
/// </para>
/// Emphasis, inline code and links inside a block's text are read by <see cref="InlineMarkdown"/>. Other Markdown is
/// shown as written.
/// </remarks>
public static partial class MessageTextParser
{
    private const int TabWidth = 4;

    // How much deeper an item must be indented than the one above to nest under it.
    private const int NestingIndent = 2;

    /// <summary>Reads <paramref name="text"/> as blocks, in order. Blank text has none.</summary>
    public static IReadOnlyList<MessageBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            reader.Read(line);
        }

        return reader.Finish();
    }

    [GeneratedRegex(@"^ {0,3}(?<marks>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:(?<bullet>[-*+•])|(?<number>\d{1,9})[.)])(?:[ \t]+(?<text>.*))?$")]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,})[ \t]*(?<language>[^\s`]*)[^`]*$")]
    private static partial Regex FenceOpenPattern();

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,})[ \t]*$")]
    private static partial Regex FenceClosePattern();

    private sealed class Reader
    {
        // The open block of code: how long its fence is, its language, and its lines so far.
        private int _fenceLength;
        private string? _fenceLanguage;
        private readonly List<string> _code = [];

        private readonly List<MessageBlock> _blocks = [];
        private readonly List<string> _paragraph = [];
        private readonly List<ListItemBlock> _items = [];

        // One entry per open nesting level of the current list: how far its items are indented, whether they are
        // numbered, and the next number.
        private readonly List<Level> _levels = [];

        public void Read(string line)
        {
            if (_fenceLength > 0)
            {
                if (FenceClosePattern().Match(line) is { Success: true } close && close.Groups["fence"].Length >= _fenceLength)
                {
                    FlushCode();
                }
                else
                {
                    _code.Add(line);
                }

                return;
            }

            if (FenceOpenPattern().Match(line) is { Success: true } fence)
            {
                FlushParagraph();
                FlushList();
                _fenceLength = fence.Groups["fence"].Length;
                _fenceLanguage = fence.Groups["language"].Value is { Length: > 0 } language ? language : null;
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                return;
            }

            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                FlushList();
                var text = heading.Groups["text"].Value.Trim();
                if (text.Length > 0)
                {
                    _blocks.Add(new HeadingBlock(heading.Groups["marks"].Length, text));
                }

                return;
            }

            if (ListItemPattern().Match(line) is { Success: true } item && CanStartItem(item))
            {
                FlushParagraph();
                AddItem(item);
                return;
            }

            var indent = IndentOf(line);
            if (_items.Count > 0 && indent > 0)
            {
                ContinueItem(line.Trim());
                return;
            }

            FlushList();
            _paragraph.Add(line.Trim());
        }

        public IReadOnlyList<MessageBlock> Finish()
        {
            FlushCode();
            FlushParagraph();
            FlushList();
            return _blocks;
        }

        // A numbered item can interrupt a paragraph only when it is 1, so that a wrapped line that begins with a
        // number, such as a year, stays in its paragraph.
        private bool CanStartItem(Match item) =>
            _paragraph.Count == 0 || !item.Groups["number"].Success || item.Groups["number"].Value.TrimStart('0') == "1";

        private void AddItem(Match item)
        {
            var indent = IndentOf(item.Groups["indent"].Value);
            var numbered = item.Groups["number"].Success;

            // Close the levels this item is not indented into, then nest it if it is indented further.
            while (_levels.Count > 1 && indent < _levels[^1].Indent)
            {
                _levels.RemoveAt(_levels.Count - 1);
            }

            if (_levels.Count == 0 || indent >= _levels[^1].Indent + NestingIndent)
            {
                _levels.Add(new Level(indent, numbered, StartNumber(item)));
            }
            else if (_levels[^1].IsNumbered != numbered)
            {
                // Switching between bullets and numbers starts a new list at this level, as in Markdown.
                if (_levels.Count == 1)
                {
                    FlushList();
                    _levels.Add(new Level(indent, numbered, StartNumber(item)));
                }
                else
                {
                    _levels[^1] = new Level(_levels[^1].Indent, numbered, StartNumber(item));
                }
            }

            var level = _levels[^1];
            var marker = numbered ? level.NextNumber.ToString(CultureInfo.InvariantCulture) + "." : ListItemBlock.Bullet;
            if (numbered)
            {
                _levels[^1] = level with { NextNumber = level.NextNumber + 1 };
            }

            _items.Add(new ListItemBlock(marker, item.Groups["text"].Value.Trim(), _levels.Count - 1));
        }

        private void ContinueItem(string text)
        {
            var last = _items[^1];
            var separator = last.Text.Length == 0 ? "" : "\n";
            _items[^1] = last with { Text = last.Text + separator + text };
        }

        // A block with no code yet is not shown: a streaming answer's fence is there before its first line.
        private void FlushCode()
        {
            if (_fenceLength == 0)
            {
                return;
            }

            var code = string.Join('\n', _code).TrimEnd('\n', ' ', '\t');
            if (code.Trim().Length > 0)
            {
                _blocks.Add(new CodeBlock(code.TrimStart('\n'), _fenceLanguage));
            }

            _fenceLength = 0;
            _fenceLanguage = null;
            _code.Clear();
        }

        private void FlushParagraph()
        {
            if (_paragraph.Count > 0)
            {
                _blocks.Add(new ParagraphBlock(string.Join('\n', _paragraph)));
                _paragraph.Clear();
            }
        }

        private void FlushList()
        {
            if (_items.Count > 0)
            {
                _blocks.Add(new ListBlock(_items.ToArray()));
                _items.Clear();
            }

            _levels.Clear();
        }

        private static long StartNumber(Match item) =>
            item.Groups["number"].Success ? long.Parse(item.Groups["number"].Value, CultureInfo.InvariantCulture) : 1;

        private static int IndentOf(string line)
        {
            var columns = 0;
            foreach (var character in line)
            {
                if (character == ' ') columns++;
                else if (character == '\t') columns += TabWidth - columns % TabWidth;
                else break;
            }

            return columns;
        }

        private readonly record struct Level(int Indent, bool IsNumbered, long NextNumber);
    }
}
