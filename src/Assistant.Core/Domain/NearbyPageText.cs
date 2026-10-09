using System.Globalization;
using System.Text;

namespace Assistant.Core.Domain;

/// <summary>Which side of the selection a stretch of page text is on, which decides the end of it that is kept when it is cut short.</summary>
public enum NearbySide
{
    /// <summary>Text just before the selection: its end, which is the part next to the selection, is kept.</summary>
    Before = 0,

    /// <summary>Text just after the selection: its start, which is the part next to the selection, is kept.</summary>
    After = 1,
}

/// <summary>
/// Cleans the page text the browser extension sends around a selection (PROJECT_SPEC §4.5, step 88) before the Assistant uses it. The
/// extension cleans it the same way; this is the second look, since the text comes from a web page and is untrusted. It removes what
/// cannot be read or is there to hide something (control characters, invisible formatting characters such as zero-width and
/// right-to-left overrides, unpaired surrogates, private-use characters), makes every run of space one space and every run of blank
/// lines one blank line, drops words so long that they are data and not prose (encoded blobs, tokens, long addresses), and cuts the
/// result to a bounded length with an ellipsis at the cut.
/// </summary>
public static class NearbyPageText
{
    /// <summary>The longest unbroken run of characters that is kept as a word; a longer one is dropped.</summary>
    public const int MaxWordLength = 80;

    private const char Ellipsis = '…';

    // How far a cut moves to land between words, so it does not leave half a word.
    private const int WordSearchLength = 30;

    /// <summary>What a context item that carries the page text around a selection is called.</summary>
    public const string ContextName = "Text around the selection";

    /// <summary>
    /// The text of the context item for the page text around a selection: the two stretches, each under a line that says which side of the
    /// selection it is from, or <see langword="null"/> when there is none.
    /// </summary>
    public static string? Describe(WebPageOrigin page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.HasNearbyContext)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(page.NearbyBefore))
        {
            parts.Add("Just before the selection:\n" + page.NearbyBefore.Trim());
        }

        if (!string.IsNullOrWhiteSpace(page.NearbyAfter))
        {
            parts.Add("Just after the selection:\n" + page.NearbyAfter.Trim());
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Cleans <paramref name="text"/> and cuts it to at most <paramref name="maxLength"/> characters.</summary>
    /// <param name="text">What the page held next to the selection, or <see langword="null"/>.</param>
    /// <param name="maxLength">The most characters to keep, including the ellipsis a cut adds.</param>
    /// <param name="side">Which side of the selection the text is on.</param>
    /// <returns>The clean text, or an empty string when nothing readable is left.</returns>
    public static string Clean(string? text, int maxLength, NearbySide side)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = Tidy(Readable(text));
        return cleaned.Length <= maxLength ? cleaned : Cut(cleaned, maxLength, side);
    }

    // Only characters that are read as text: line breaks and spaces are kept as such, controls and invisible or unassigned characters go.
    private static string Readable(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar)
            {
                continue;
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                    switch (rune.Value)
                    {
                        case '\n' or '\r' or 0x85:
                            builder.Append('\n');
                            break;
                        case '\t' or '\v' or '\f':
                            builder.Append(' ');
                            break;
                    }

                    break;
                case UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned:
                    break;
                case UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                    builder.Append('\n');
                    break;
                case UnicodeCategory.SpaceSeparator:
                    builder.Append(' ');
                    break;
                default:
                    builder.Append(rune.ToString());
                    break;
            }
        }

        return builder.ToString();
    }

    // One space between words, no words that are data, no empty line but a single one between paragraphs.
    private static string Tidy(string text)
    {
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in text.Split('\n'))
        {
            var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length <= MaxWordLength);
            var line = string.Join(' ', words);
            if (line.Length == 0)
            {
                blank = lines.Count > 0;
                continue;
            }

            if (blank)
            {
                lines.Add(string.Empty);
                blank = false;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static string Cut(string text, int maxLength, NearbySide side)
    {
        var keep = maxLength - 1;
        if (side == NearbySide.Before)
        {
            var start = text.Length - keep;
            start = char.IsLowSurrogate(text[start]) ? start + 1 : start;
            if (!char.IsWhiteSpace(text[start - 1]) && !char.IsWhiteSpace(text[start]))
            {
                var space = text.IndexOfAny([' ', '\n'], start, Math.Min(WordSearchLength, text.Length - start));
                start = space >= 0 ? space + 1 : start;
            }

            return Ellipsis + text[start..].TrimStart();
        }

        var end = keep;
        end = char.IsHighSurrogate(text[end - 1]) ? end - 1 : end;
        if (!char.IsWhiteSpace(text[end - 1]) && !char.IsWhiteSpace(text[end]))
        {
            var space = text.LastIndexOfAny([' ', '\n'], end - 1, Math.Min(WordSearchLength, end));
            end = space > 0 ? space : end;
        }

        return text[..end].TrimEnd() + Ellipsis;
    }
}
