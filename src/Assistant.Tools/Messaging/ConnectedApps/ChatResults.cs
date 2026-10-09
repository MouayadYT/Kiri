using System.Text;
using System.Text.Json;
using Assistant.Core.People;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Messaging.ConnectedApps;

/// <summary>A chat a connected messaging app listed.</summary>
/// <param name="Id">The app's id for the chat, which only the app's own send tool takes.</param>
/// <param name="Title">The chat's name in the app; empty when it has none.</param>
/// <param name="Network">The messaging service it is on ("WhatsApp", "Signal"), when the app says; empty otherwise.</param>
/// <param name="IsGroup"><see langword="true"/> for a group, <see langword="false"/> for a chat with one person, <see langword="null"/> when the app does not say.</param>
/// <param name="Facts">Every text and number the chat object held, folded to letters and digits, so that a saved number or username can be looked for in it.</param>
/// <param name="IsArchived">Whether the app says the chat is archived (put away by the user): an old chat that is left over is not the one meant when a live one has the same name.</param>
internal sealed record ChatInfo(string Id, string Title, string Network, bool? IsGroup, string Facts, bool IsArchived = false)
{
    // Keeps the chat's name and facts (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsGroup = {IsGroup}");
        return true;
    }
}

/// <summary>
/// Reads what a connected messaging app's search for chats answered (PROJECT_SPEC §4.8, step 116). Servers answer as they please, so this reads the shapes they commonly use: JSON, as
/// text or as structured content, with the chats in an array (at the top or under a name such as <c>chats</c>, <c>results</c> or <c>items</c>), each an object with an id and usually a
/// title, a service and some say of whether it is a group. What it cannot read is no chat, never a guess: an answer that is not understood finds no one, and nothing is sent to anyone.
/// Everything in it is data from the app, so none of it is an instruction.
/// </summary>
internal static class ChatResults
{
    private const int MaxChats = 50;
    private const int MaxDepth = 5;
    private const int MaxFactsLength = 4000;

    private static readonly string[] IdKeys = ["id", "chat_id", "chatid", "conversation_id", "conversationid", "thread_id", "threadid", "room_id", "roomid", "jid", "guid", "chatguid"];
    private static readonly string[] TitleKeys =
        ["title", "name", "display_name", "displayname", "subject", "label", "chat_name", "chatname", "chat_title", "chattitle", "contact_name", "contactname", "full_name", "fullname"];
    private static readonly string[] NetworkKeys = ["network", "service", "platform", "protocol", "provider", "account_id", "accountid", "account"];
    private static readonly string[] GroupFlagKeys = ["is_group", "isgroup", "group", "is_group_chat", "isgroupchat"];
    private static readonly string[] TypeKeys = ["type", "kind", "chat_type", "chattype"];
    private static readonly string[] ArchivedKeys = ["is_archived", "isarchived", "archived"];
    private static readonly string[] CountKeys = ["participant_count", "participants_count", "member_count", "members_count", "participantcount", "membercount"];
    private static readonly string[] ParticipantKeys = ["participants", "members", "participant_ids", "attendees"];

    private static readonly string[] GroupTypes = ["group", "channel", "broadcast", "supergroup", "community", "room"];
    private static readonly string[] DirectTypes = ["single", "direct", "dm", "private", "individual", "one_to_one", "user", "contact", "person", "personal"];

    /// <summary>The chats <paramref name="result"/> lists, at most <see cref="MaxChats"/>; none when it lists none or is not understood.</summary>
    public static IReadOnlyList<ChatInfo> Parse(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var chats = new List<ChatInfo>();
        if (result.IsError)
        {
            return chats;
        }

        if (result.StructuredContent is { } structured)
        {
            Collect(structured, chats, 0);
        }

        var prose = new List<string>();
        foreach (var block in result.Content)
        {
            if (chats.Count >= MaxChats)
            {
                break;
            }

            if (block.Kind is McpContentKind.Text or McpContentKind.Resource && block.Text is { Length: > 0 } text)
            {
                var isJson = false;
                if (Looksjson(text))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        Collect(document.RootElement, chats, 0);
                        isJson = true;
                    }
                    catch (JsonException)
                    {
                        // Not JSON as a whole: what is JSON inside it is looked for below.
                    }
                }

                if (!isJson)
                {
                    // An app that tells the model how to use its answer writes words around the data: the JSON inside the words is read, and failing that the words themselves.
                    CollectEmbedded(text, chats);
                    prose.Add(text);
                }
            }
        }

        if (chats.Count == 0)
        {
            foreach (var text in prose)
            {
                chats.AddRange(ChatTextResults.Parse(text));
            }
        }

        // The same chat found twice (structured content and text that say the same) is one.
        return [.. chats.GroupBy(chat => chat.Id, StringComparer.Ordinal).Select(group => group.First()).Take(MaxChats)];
    }

    /// <summary>Whether the text of a result says in words that the message is waiting or being sent and has not arrived yet (<c>pending</c>, <c>queued</c>, <c>sending</c>).</summary>
    public static bool SaysPending(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var words = new StringBuilder();
        foreach (var text in result.Content.Where(block => block.Kind == McpContentKind.Text).Select(block => block.Text).Append(result.StructuredContent?.GetRawText()))
        {
            words.Append(text).Append(' ');
        }

        var lower = words.ToString().ToLowerInvariant();
        return lower.Contains("pending", StringComparison.Ordinal) || lower.Contains("queued", StringComparison.Ordinal) || lower.Contains("\"sending\"", StringComparison.Ordinal);
    }

    private static bool Looksjson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 1 && trimmed[0] is '{' or '[';
    }

    // The JSON values that stand inside a text that is not JSON as a whole ("Found 1 chat:" and then the chat), read one after another from the first bracket on.
    private static void CollectEmbedded(string text, List<ChatInfo> chats)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var position = 0;
        for (var attempt = 0; attempt < 12 && position < bytes.Length && chats.Count < MaxChats; attempt++)
        {
            var start = Array.FindIndex(bytes, position, value => value is (byte)'{' or (byte)'[');
            if (start < 0)
            {
                return;
            }

            var consumed = 0;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(start), isFinalBlock: true, state: default);
                if (JsonDocument.TryParseValue(ref reader, out var document))
                {
                    using (document)
                    {
                        Collect(document.RootElement, chats, 0);
                    }

                    consumed = (int)reader.BytesConsumed;
                }
            }
            catch (JsonException)
            {
                // A bracket that starts no JSON (a "[1]" in a sentence) is passed over.
            }

            position = start + Math.Max(consumed, 1);
        }
    }

    private static void Collect(JsonElement element, List<ChatInfo> chats, int depth)
    {
        if (depth > MaxDepth || chats.Count >= MaxChats)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Any(item => item.ValueKind == JsonValueKind.Object && IdOf(item) is not null))
                {
                    foreach (var item in items.Where(item => item.ValueKind == JsonValueKind.Object))
                    {
                        if (ChatOf(item) is { } chat)
                        {
                            chats.Add(chat);
                        }
                    }

                    return;
                }

                foreach (var item in items)
                {
                    Collect(item, chats, depth + 1);
                }

                break;

            case JsonValueKind.Object:
                // An object with an id that holds no list of chats of its own is a chat; anything else is looked through.
                if (IdOf(element) is not null && !element.EnumerateObject().Any(property => HoldsChats(property.Value)))
                {
                    if (ChatOf(element) is { } chat)
                    {
                        chats.Add(chat);
                    }

                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, chats, depth + 1);
                }

                break;
        }
    }

    private static bool HoldsChats(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && IdOf(item) is not null);

    private static string? IdOf(JsonElement chat)
    {
        foreach (var key in IdKeys)
        {
            if (TryGet(chat, key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (!string.IsNullOrWhiteSpace(id) && id.Length <= 300)
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static ChatInfo? ChatOf(JsonElement chat)
    {
        if (IdOf(chat) is not { } id)
        {
            return null;
        }

        var title = TextOf(chat, TitleKeys);
        var network = TextOf(chat, NetworkKeys);
        var facts = new StringBuilder();
        AddFacts(chat, facts, 0);
        return new ChatInfo(id, title, network, IsGroupOf(chat), facts.ToString(), IsArchivedOf(chat));
    }

    private static bool IsArchivedOf(JsonElement chat)
    {
        foreach (var key in ArchivedKeys)
        {
            if (TryGet(chat, key, out var flag) && flag.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private static bool? IsGroupOf(JsonElement chat)
    {
        foreach (var key in GroupFlagKeys)
        {
            if (TryGet(chat, key, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return flag.GetBoolean();
            }
        }

        var type = TextOf(chat, TypeKeys).ToLowerInvariant();
        if (type.Length > 0)
        {
            if (GroupTypes.Any(word => type.Contains(word, StringComparison.Ordinal)))
            {
                return true;
            }

            if (DirectTypes.Any(word => type.Contains(word, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        foreach (var key in CountKeys)
        {
            if (TryGet(chat, key, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number))
            {
                return number > 2;
            }
        }

        foreach (var key in ParticipantKeys)
        {
            if (TryGet(chat, key, out var people) && people.ValueKind == JsonValueKind.Array)
            {
                // The user is one of the participants: two is a chat between two people.
                var count = people.GetArrayLength();
                return count > 2;
            }
        }

        return null;
    }

    private static string TextOf(JsonElement chat, string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(chat, key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return Line(text, 120);
            }
        }

        return string.Empty;
    }

    // A property by name, whatever its case.
    private static bool TryGet(JsonElement element, string key, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // Every text and number inside the chat, folded, so that a saved number or username is found wherever the app put it.
    private static void AddFacts(JsonElement element, StringBuilder facts, int depth)
    {
        if (depth > MaxDepth || facts.Length >= MaxFactsLength)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                facts.Append(' ').Append(PersonText.Fold(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                facts.Append(' ').Append(element.GetRawText());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddFacts(item, facts, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddFacts(property.Value, facts, depth + 1);
                }

                break;
        }
    }

    // A text from the app, as one line of at most the length: control characters and angle brackets taken out.
    private static string Line(string text, int maxLength)
    {
        var builder = new StringBuilder();
        var lastWasSpace = true;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '<' or '>')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }
}
