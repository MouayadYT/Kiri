using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Voice;

/// <summary>
/// Turns a piece of the Assistant's written answer into words that are good to hear (PROJECT_SPEC §4.2, step 125): the answer is Markdown, and a
/// voice that reads <c>**bold**</c> as "star star bold star star", or a link's address letter by letter, is worse than none. The text of the answer
/// itself is never changed: this reads a copy, per segment, on the way to the speech engine.
/// </summary>
public static partial class SpeechTextCleaner
{
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    [GeneratedRegex(@"<[^>\n]{1,80}>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:[-*+•]|\d{1,3}[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^\s*>+\s?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\s*\|?[\s:|-]{3,}\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"(\*\*|__|~~|\*|`)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?<=[A-Za-z0-9])_(?=[A-Za-z0-9])")]
    private static partial Regex WordUnderscore();

    [GeneratedRegex(@"(?<![A-Za-z0-9])_+|_+(?![A-Za-z0-9])")]
    private static partial Regex LoneUnderscore();

    [GeneratedRegex(@"(?<=\d)\s*[*×]\s*(?=\d)")]
    private static partial Regex Times();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?:\s*,){2,}")]
    private static partial Regex RepeatedCommas();

    /// <summary>
    /// The speakable words of <paramref name="text"/>: Markdown marks, links, addresses, tags and pictures' descriptions of structure removed,
    /// lists and tables made plain, and spaces collapsed. Empty when nothing in it can be said, such as a rule or a lone bullet.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder(text.Length);
        foreach (var raw in lines)
        {
            var line = CleanLine(raw);
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                // A new line is a pause, as it is on the page.
                if (!EndsSentence(builder))
                {
                    builder.Append('.');
                }

                builder.Append(' ');
            }

            builder.Append(line);
        }

        return Finish(builder.ToString());
    }

    private static string CleanLine(string raw)
    {
        if (TableRule().IsMatch(raw) && raw.Contains('-', StringComparison.Ordinal))
        {
            return "";
        }

        var line = Quote().Replace(Heading().Replace(raw, ""), "");
        line = ListMarker().Replace(line, "");
        if (line.Contains('|', StringComparison.Ordinal))
        {
            line = string.Join(", ", line.Split('|').Select(cell => cell.Trim()).Where(cell => cell.Length > 0));
        }

        line = Times().Replace(line, " times ");
        line = Image().Replace(line, "$1");
        line = Link().Replace(line, "$1");
        line = Address().Replace(line, "");
        line = Tag().Replace(line, "");
        line = Emphasis().Replace(line, "");
        line = WordUnderscore().Replace(line, " ");
        line = LoneUnderscore().Replace(line, "");
        return line.Trim();
    }

    private static string Finish(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '—' or '–' or '‒':
                    builder.Append(", ");
                    continue;
                case '…':
                    builder.Append('.');
                    continue;
                case '&':
                    builder.Append(" and ");
                    continue;
                case ' ' or ' ':
                    builder.Append(' ');
                    continue;
                case '~' or '^' or '\\' or '​' or '‍' or '️':
                    continue;
            }

            // Emoji, arrows, box drawing and the like have nothing to say.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Control)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var spoken = Spaces().Replace(builder.ToString(), " ").Trim();
        spoken = RepeatedCommas().Replace(spoken, ",");
        spoken = spoken.Replace(" ,", ",", StringComparison.Ordinal).Replace(" .", ".", StringComparison.Ordinal);
        spoken = spoken.Trim(',', ' ');
        return HasSpeakableCharacter(spoken) ? spoken : "";
    }

    private static bool EndsSentence(StringBuilder builder)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            var c = builder[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c is '.' or '!' or '?' or ':' or ';' or ',';
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/> has a letter or a digit, which a voice can say.</summary>
    internal static bool HasSpeakableCharacter(string text) => text.Any(char.IsLetterOrDigit);
}
