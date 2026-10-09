using System.Text.RegularExpressions;
using Assistant.Core.People;

namespace Assistant.Tools.Messaging.ConnectedApps;

/// <summary>
/// Reads a connected messaging app's list of chats when it was answered in words and not in JSON (a heading or a bullet for each chat, with "chatID: …" and "type: single" lines, or
/// one line a chat). Some apps tell the model how to use the chats they found, so their answer is prose around the data. A chat is only read when the text gives it an id; the
/// chat's name is taken from a "title:" or "name:" line or from the head of its first line, and what is not found stays empty: a chat with no name is never taken for anyone, since the
/// caller only keeps chats named like the person. Everything in it is data from the app, so none of it is an instruction.
/// </summary>
internal static partial class ChatTextResults
{
    private const int MaxChats = 50;
    private const int MaxIdLength = 300;
    private const int MaxFactsLength = 4000;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Beeper and other Matrix-based apps write a chat's id as !opaque:server.
    [GeneratedRegex(@"![A-Za-z0-9_=.\-]{3,}:[A-Za-z0-9_.\-]{3,}", RegexOptions.CultureInvariant, 250)]
    private static partial Regex MatrixId();

    // "chatID: abc", "chat_id=abc", "id: abc", with or without quotes or Markdown around the value.
    [GeneratedRegex(@"\b(?:chat[ _\-]?id|id)\b\**\s*[:=]\s*\**[`""']?([^\s,;|`""')\]>*]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex LabelledId();

    [GeneratedRegex(@"^[\s>*•#\-]*(?:\*\*)?(?:title|name|chat name|chat title|display name)(?:\*\*)?\s*[:=]\s*(?:\*\*)?(.+?)(?:\*\*)?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 250)]
    private static partial Regex LabelledTitle();

    [GeneratedRegex(@"\b(?:type|kind)\b\**\s*[:=]\s*\**\s*([A-Za-z_\-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex LabelledType();

    [GeneratedRegex(@"\b(?:network|service|platform)\b\**\s*[:=]\s*\**\s*([^\r\n,;|()*]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex LabelledNetwork();

    // "Chat on Beeper (Matrix) (matrix) with Savannah, marcus.": the network as the app names it, and then the account in brackets.
    [GeneratedRegex(@"\bchat on (.+?) \(([^()\r\n]+)\) with\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex ChatOn();

    [GeneratedRegex(@"\bthis chat is archived\b|\barchived\b\**\s*[:=]\s*\**\s*(?:true|yes)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex SaysArchived();

    [GeneratedRegex(@"\b(?:single|direct|dm|one[- ]to[- ]one)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex SaysDirect();

    [GeneratedRegex(@"\b(?:group|channel|broadcast)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex SaysGroup();

    [GeneratedRegex(@"^[\s>*•#\-]+|^\d+[.)]\s+", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ListMarker();

    /// <summary>The chats <paramref name="text"/> lists, at most 50; none when it gives no chat an id.</summary>
    public static IReadOnlyList<ChatInfo> Parse(string text)
    {
        var chats = new List<ChatInfo>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chats;
        }

        try
        {
            foreach (var record in Records(text))
            {
                if (chats.Count >= MaxChats)
                {
                    break;
                }

                if (ChatOf(record) is { } chat)
                {
                    chats.Add(chat);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Text that takes too long to read is read no further.
        }

        return chats;
    }

    // One record for each chat: a block of the text (set apart by blank lines) that holds one id is a record; a block with several is cut at the lines that hold an id.
    private static IEnumerable<string> Records(string text)
    {
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n", StringComparison.Ordinal), @"\n[ \t]*\n", RegexOptions.CultureInvariant, MatchTimeout))
        {
            var lines = block.Split('\n');
            var withId = lines.Count(line => IdIn(line) is not null);
            if (withId <= 1)
            {
                if (withId == 1)
                {
                    yield return block;
                }

                continue;
            }

            var current = new List<string>();
            foreach (var line in lines)
            {
                if (IdIn(line) is not null && current.Any(held => IdIn(held) is not null))
                {
                    yield return string.Join('\n', current);
                    current.Clear();
                }

                current.Add(line);
            }

            if (current.Any(held => IdIn(held) is not null))
            {
                yield return string.Join('\n', current);
            }
        }
    }

    private static string? IdIn(string text)
    {
        // A word after "id:" is an id only when it looks like one (a digit, or a mark such as - _ : ! @ #), so that "use the id: to look one up" names no chat.
        var labelled = LabelledId().Match(text);
        if (labelled.Success && Usable(labelled.Groups[1].Value) && labelled.Groups[1].Value.Any(character => char.IsDigit(character) || character is '-' or '_' or ':' or '!' or '@' or '#'))
        {
            return labelled.Groups[1].Value;
        }

        var matrix = MatrixId().Match(text);
        return matrix.Success && Usable(matrix.Value) ? matrix.Value : null;
    }

    private static bool Usable(string id) => id.Length is > 0 and <= MaxIdLength && id.Any(char.IsLetterOrDigit);

    private static ChatInfo? ChatOf(string record)
    {
        if (IdIn(record) is not { } id)
        {
            return null;
        }

        var facts = PersonText.Fold(record);
        return new ChatInfo(
            id, TitleOf(record, id), NetworkOf(record), IsGroupOf(record), facts.Length > MaxFactsLength ? facts[..MaxFactsLength] : facts, SaysArchived().IsMatch(record));
    }

    private static string TitleOf(string record, string id)
    {
        if (LabelledTitle().Match(record) is { Success: true } labelled && Line(labelled.Groups[1].Value) is { Length: > 0 } named)
        {
            return named;
        }

        var first = record.Split('\n').FirstOrDefault(line => line.Trim().Length > 0) ?? string.Empty;
        first = ListMarker().Replace(first, string.Empty).Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

        // The head of the line: what comes before the id, a bracket, a dash or a bar.
        var end = first.Length;
        foreach (var stop in new[] { id, " (", " [", " - ", " – ", " — ", " | ", "chatID", "chat_id", "id:", "ID:" })
        {
            var at = first.IndexOf(stop, StringComparison.Ordinal);
            if (at >= 0 && at < end)
            {
                end = at;
            }
        }

        var head = first[..end].Trim().TrimEnd(':', '-', ',', ';').Trim();
        return IsLabel(head) ? string.Empty : Line(head);
    }

    // A line that is only a label ("Chat", "Results", "chatID") is not a name.
    private static bool IsLabel(string head) =>
        head.Length == 0
        || head.EndsWith(':')
        || PersonText.Fold(head) is "chat" or "chats" or "result" or "results" or "chatid" or "id" or "found" or "type" or "network" or "title" or "name";

    private static string NetworkOf(string record)
    {
        if (LabelledNetwork().Match(record) is { Success: true } labelled)
        {
            return Line(labelled.Groups[1].Value);
        }

        return ChatOn().Match(record) is { Success: true } chatOn ? Line(chatOn.Groups[1].Value) : string.Empty;
    }

    private static bool? IsGroupOf(string record)
    {
        if (LabelledType().Match(record) is { Success: true } labelled)
        {
            var type = labelled.Groups[1].Value.ToLowerInvariant();
            if (SaysGroup().IsMatch(type))
            {
                return true;
            }

            if (SaysDirect().IsMatch(type) || type is "private" or "individual" or "user" or "person")
            {
                return false;
            }
        }

        // A record that says "single" is a chat with one person; one that says "group" is a group; one that says both is not taken for either.
        var direct = SaysDirect().IsMatch(record);
        var group = SaysGroup().IsMatch(record);
        return direct == group ? null : group;
    }

    // A text from the app as one short line, with no angle brackets or control characters.
    private static string Line(string text)
    {
        var cleaned = new string(text.Select(character => char.IsControl(character) || character is '<' or '>' ? ' ' : character).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }
}
