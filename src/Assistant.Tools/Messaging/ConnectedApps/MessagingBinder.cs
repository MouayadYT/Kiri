using System.Text.Json;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Messaging.ConnectedApps;

/// <summary>What the messaging tool of a connected app is sent to: a chat it knows by an id, or an address (a number, a username) the message is simply addressed to.</summary>
internal enum SendTarget
{
    /// <summary>The tool takes the id of a chat, which the app's own search finds.</summary>
    ChatId = 0,

    /// <summary>The tool takes a number, an address or a username the message is sent to.</summary>
    Address = 1,
}

/// <summary>The tool of a connected app that sends a text, and the names of its two arguments.</summary>
/// <param name="Tool">The tool as the server listed it.</param>
/// <param name="TargetArgument">The argument that says who or where (a chat id, or an address), by the server's name.</param>
/// <param name="TextArgument">The argument that holds the text, by the server's name.</param>
/// <param name="Target">What the target argument is.</param>
internal sealed record SendBinding(McpToolDescriptor Tool, string TargetArgument, string TextArgument, SendTarget Target);

/// <summary>The tool of a connected app that looks for chats, and the name of its one argument.</summary>
/// <param name="Tool">The tool as the server listed it.</param>
/// <param name="QueryArgument">The argument that holds what to look for, by the server's name.</param>
internal sealed record FindBinding(McpToolDescriptor Tool, string QueryArgument);

/// <summary>The two tools of a connected messaging app that the Assistant's own messaging uses.</summary>
/// <param name="Send">The tool that sends.</param>
/// <param name="Find">The tool that looks for chats; <see langword="null"/> for an app whose send tool is addressed directly.</param>
internal sealed record MessagingBinding(SendBinding Send, FindBinding? Find);

/// <summary>
/// Finds, among the tools of a connected app, the two the Assistant's messaging needs (PROJECT_SPEC §4.8, step 116): the one that sends a text, and, when that takes a chat's id,
/// the one that looks chats up. It reads the words of the tools' names and the names of their arguments by fixed lists, no model and no network, and it is strict: a tool needs
/// nothing but the text and who it goes to (an argument it must be given and cannot be told, such as an account, makes the tool unusable), a tool that drafts, schedules, edits
/// or deletes is never the one that sends, and a tool that looks for <i>messages</i> is not one that looks for chats. No tool is bound that is not understood, so an app that is not
/// understood is simply not used for messaging; the model, which may still use the app's own tools for what the user asks, is never made to guess.
/// </summary>
internal static class MessagingBinder
{
    private static readonly IntegrationCapability SendsMessage = new(CapabilityAction.Send, "message");

    // A tool that has one of these words in its name is not the one that sends a text.
    private static readonly HashSet<string> NotSending = new(StringComparer.Ordinal)
    {
        "draft", "delete", "remove", "edit", "update", "schedule", "react", "reaction", "mark", "read", "list", "get", "search", "find", "forward", "archive", "pin", "mute", "typing",
    };

    // The words of a tool that looks chats or contacts up, and of what it must not be about.
    private static readonly HashSet<string> ChatWords = new(StringComparer.Ordinal) { "chat", "conversation", "thread", "contact", "dialog", "person", "people", "inbox" };

    private static readonly HashSet<string> LookupWords = new(StringComparer.Ordinal) { "search", "find", "list", "lookup", "get", "query", "resolve", "match" };

    private static readonly HashSet<string> NotChatLookup = new(StringComparer.Ordinal)
    {
        "message", "send", "create", "delete", "update", "edit", "read", "mark", "archive", "mute", "pin", "draft", "reply", "forward", "react",
    };

    private static readonly string[] TextArguments = ["text", "message", "body", "content", "messagetext", "msg", "textbody"];

    private static readonly string[] ChatIdArguments =
        ["chatid", "chat", "conversationid", "conversation", "threadid", "thread", "roomid", "room", "channelid", "channel", "jid", "dialogid", "chatguid"];

    private static readonly string[] AddressArguments =
        ["recipient", "to", "phone", "phonenumber", "number", "contact", "address", "handle", "receiver", "destination", "username", "user", "recipientid"];

    private static readonly string[] QueryArguments =
        ["query", "search", "q", "name", "text", "keyword", "term", "filter", "contact", "phone", "number", "user", "username", "identifier", "searchterm", "searchtext", "querystring"];

    /// <summary>Whether the tool names the registry kept for an app (no schemas, no descriptions) include one that could be the tool that sends a text.</summary>
    public static bool NamesSuggestSending(IReadOnlyList<string> toolNames) =>
        toolNames.Count > 0 && SendCandidates(toolNames.Select(name => new ToolFacts(name)).ToList()).Count > 0;

    /// <summary>The tools of <paramref name="tools"/> that send and look up chats, or <see langword="null"/> when they do not have a pair that can be used.</summary>
    public static MessagingBinding? Bind(IReadOnlyList<McpToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var send = BindSend(tools);
        if (send is null)
        {
            return null;
        }

        if (send.Target == SendTarget.Address)
        {
            return new MessagingBinding(send, null);
        }

        // A tool that takes a chat's id needs the one that finds the id.
        return BindFind(tools) is { } find ? new MessagingBinding(send, find) : null;
    }

    /// <summary>Whether the tool takes words for a chat's draft: an argument named for a draft (<c>draftText</c>, <c>draft_text</c>).</summary>
    public static bool WritesDraft(McpToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Properties(tool.InputSchema).Any(property => Fold(property.Name).StartsWith("draft", StringComparison.Ordinal));
    }

    private static SendBinding? BindSend(IReadOnlyList<McpToolDescriptor> tools)
    {
        var facts = tools.Select(tool => new ToolFacts(tool.Name, tool.Title, tool.Description)).ToList();
        foreach (var name in SendCandidates(facts))
        {
            var tool = tools.First(candidate => candidate.Name == name);
            var properties = Properties(tool.InputSchema);
            var text = First(properties, TextArguments);
            var chat = First(properties, ChatIdArguments);
            var address = chat is null ? First(properties, AddressArguments) : null;
            var target = chat ?? address;
            if (text is null || target is null || !OnlyNeeds(tool.InputSchema, text, target))
            {
                continue;
            }

            return new SendBinding(tool, target, text, chat is not null ? SendTarget.ChatId : SendTarget.Address);
        }

        return null;
    }

    // The names of the tools that could send a text, best first: one named for sending before the others.
    private static List<string> SendCandidates(IReadOnlyList<ToolFacts> facts)
    {
        var names = CapabilityMatcher.Match(SendsMessage, facts)
            .Where(name => !CapabilityMatcher.Stems(name).Overlaps(NotSending))
            .ToList();
        return [.. names.OrderBy(name => CapabilityMatcher.Stems(name).Contains("send") ? 0 : 1)];
    }

    private static FindBinding? BindFind(IReadOnlyList<McpToolDescriptor> tools)
    {
        var candidates = tools
            .Select(tool => (Tool: tool, Words: CapabilityMatcher.Stems(tool.Name)))
            .Where(entry => entry.Words.Overlaps(ChatWords) && entry.Words.Overlaps(LookupWords) && !entry.Words.Overlaps(NotChatLookup))
            .OrderBy(entry => entry.Words.Contains("search") || entry.Words.Contains("find") ? 0 : 1);
        foreach (var (tool, _) in candidates)
        {
            var properties = Properties(tool.InputSchema);
            if (First(properties, QueryArguments) is { } query && OnlyNeeds(tool.InputSchema, query))
            {
                return new FindBinding(tool, query);
            }
        }

        return null;
    }

    // The first of the argument names, in the order of the list, that the tool has as a text; the server's own spelling of it.
    private static string? First(IReadOnlyList<(string Name, bool IsText)> properties, string[] wanted)
    {
        foreach (var name in wanted)
        {
            foreach (var property in properties)
            {
                if (property.IsText && Fold(property.Name) == name)
                {
                    return property.Name;
                }
            }
        }

        return null;
    }

    // Whether every argument the tool must be given is among the ones the Assistant can fill.
    private static bool OnlyNeeds(JsonElement schema, params string[] known)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return required.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && known.Contains(item.GetString(), StringComparer.Ordinal));
    }

    // The tool's arguments by the server's names, and whether each is a text (a string, or one that says no type).
    private static List<(string Name, bool IsText)> Properties(JsonElement schema)
    {
        var properties = new List<(string, bool)>();
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var found) || found.ValueKind != JsonValueKind.Object)
        {
            return properties;
        }

        foreach (var property in found.EnumerateObject())
        {
            var isText = !property.Value.TryGetProperty("type", out var type) || type.ValueKind == JsonValueKind.String && type.GetString() == "string";
            properties.Add((property.Name, isText));
        }

        return properties;
    }

    // An argument's name as the lists spell it: lower case, letters and digits only.
    private static string Fold(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
