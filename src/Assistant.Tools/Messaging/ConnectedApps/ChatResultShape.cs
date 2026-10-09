using System.Text;
using System.Text.Json;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Messaging.ConnectedApps;

/// <summary>
/// What an app's answer looks like, with none of what it says: for the log, when a search for chats answered and no chat could be read from it. JSON is drawn as its keys and the kinds
/// of its values, and words as their punctuation and layout with every letter and digit replaced, except for a short list of words that are names of fields or of the kinds of chat
/// (chatID, title, single, group, WhatsApp). So the log says how an app answers without holding a name, a number, an id or a message (PROJECT_SPEC §3.3).
/// </summary>
internal static class ChatResultShape
{
    private const int MaxLength = 900;
    private const int MaxDepth = 6;

    // Words that are the app's own vocabulary, never a person's: these are kept as they are.
    private static readonly HashSet<string> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "ids", "chat", "chats", "chatid", "chat_id", "accountid", "account_id", "account", "accounts", "network", "service", "platform", "title", "name", "type", "kind", "single",
        "group", "direct", "dm", "participants", "participant", "members", "total", "count", "unread", "unreadcount", "muted", "pinned", "archived", "isarchived", "ismuted", "ispinned",
        "lastactivity", "items", "results", "result", "found", "use", "send", "message", "messages", "with", "to", "the", "a", "of", "and", "for", "from", "in", "is", "are", "query",
        "scope", "titles", "hasmore", "true", "false", "null", "no", "none", "error", "text", "content", "beeper", "whatsapp", "signal", "telegram", "instagram", "messenger", "discord",
        "slack", "imessage", "matrix", "sms", "facebook", "linkedin", "twitter", "x", "gmessages", "googlemessages", "limit", "cursor", "next", "page", "more", "see", "tool", "instructions",
        "important", "note", "following", "details", "info", "list", "name", "local", "chatname", "isgroup", "is_group", "participantcount", "member_count", "inbox", "primary", "archive",
    };

    // The values of these keys are kinds, never private: they are kept in the picture of a JSON answer.
    private static readonly HashSet<string> KindKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "kind", "chat_type", "chattype", "network", "service", "platform", "protocol", "is_group", "isgroup", "group",
    };

    /// <summary>A picture of <paramref name="result"/> that holds its layout and field names and none of its content, at most about 900 characters.</summary>
    public static string Describe(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var picture = new StringBuilder();
        picture.Append("error=").Append(result.IsError ? "yes" : "no");
        if (result.StructuredContent is { } structured)
        {
            picture.Append("; structured=");
            Json(structured, picture, 0, null);
        }

        var index = 0;
        foreach (var block in result.Content.Take(4))
        {
            index++;
            picture.Append("; block").Append(index).Append('=').Append(block.Kind);
            if (block.Text is { } text)
            {
                picture.Append('(').Append(text.Length).Append(" chars)=");
                picture.Append(Looksjson(text) && TryJson(text, out var document) ? Shape(document) : Words(text));
            }
        }

        if (result.Content.Count > 4)
        {
            picture.Append("; ").Append(result.Content.Count - 4).Append(" more blocks");
        }

        return picture.Length > MaxLength ? picture.ToString(0, MaxLength) + "…" : picture.ToString();
    }

    private static bool Looksjson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 1 && trimmed[0] is '{' or '[';
    }

    private static bool TryJson(string text, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static string Shape(JsonElement element)
    {
        var picture = new StringBuilder();
        Json(element, picture, 0, null);
        return picture.ToString();
    }

    private static void Json(JsonElement element, StringBuilder picture, int depth, string? key)
    {
        if (picture.Length > MaxLength)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= MaxDepth)
                {
                    picture.Append("{…}");
                    return;
                }

                picture.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().Take(30))
                {
                    picture.Append(first ? string.Empty : ",");
                    first = false;

                    // A key that is not a plain field name (an id or a name used as a key) is not written.
                    picture.Append(IsFieldName(property.Name) ? property.Name : "~").Append(':');
                    Json(property.Value, picture, depth + 1, property.Name);
                }

                picture.Append('}');
                break;
            case JsonValueKind.Array:
                var count = element.GetArrayLength();
                picture.Append('[');
                if (count > 0)
                {
                    if (depth >= MaxDepth)
                    {
                        picture.Append('…');
                    }
                    else
                    {
                        Json(element.EnumerateArray().First(), picture, depth + 1, key);
                    }
                }

                picture.Append(']').Append('×').Append(count);
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (key is not null && KindKeys.Contains(key) && value.Length <= 30 && value.All(character => char.IsLetter(character) || character is ' ' or '-' or '_'))
                {
                    picture.Append('"').Append(value).Append('"');
                }
                else
                {
                    picture.Append("s").Append(value.Length);
                }

                break;
            case JsonValueKind.Number:
                picture.Append('0');
                break;
            case JsonValueKind.True:
                picture.Append("true");
                break;
            case JsonValueKind.False:
                picture.Append("false");
                break;
            default:
                picture.Append("null");
                break;
        }
    }

    private static bool IsFieldName(string name) => name.Length is > 0 and <= 30 && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    // Words as their layout: the app's own vocabulary kept, every other letter written as x (X for a capital) and every digit as 9; line breaks as \n.
    private static string Words(string text)
    {
        var picture = new StringBuilder();
        var index = 0;
        while (index < text.Length && picture.Length < MaxLength)
        {
            var character = text[index];
            if (char.IsLetter(character) || character == '_')
            {
                var end = index;
                while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '_'))
                {
                    end++;
                }

                var word = text[index..end];
                if (Vocabulary.Contains(word))
                {
                    picture.Append(word);
                }
                else
                {
                    foreach (var letter in word)
                    {
                        picture.Append(letter == '_' ? '_' : char.IsUpper(letter) ? 'X' : 'x');
                    }
                }

                index = end;
                continue;
            }

            picture.Append(character switch
            {
                '\n' => "\\n",
                '\r' => string.Empty,
                '\t' => "\\t",
                _ when char.IsDigit(character) => "9",
                _ when char.IsControl(character) => " ",
                _ => character.ToString(),
            });
            index++;
        }

        return picture.ToString();
    }
}
