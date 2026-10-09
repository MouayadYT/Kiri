using System.Text.RegularExpressions;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The selector of connected apps and tools by words (step 104): the words of the request are compared with the words of an app's name and id and
/// with the names (and, once the tools are known, the titles and descriptions) of its tools. A word is lower case and loses a plural "s"; short words and
/// the common words of a request ("the", "my", "please") do not count. An app is worth connecting to when its name or id is in the request, or when the
/// request has at least two words of its tools' names; its tools are the ones that share a word with the request, strongest first, and when the app
/// is named but no tool shares a word, its first few. A tool that does what the request asks in the app (the request is read as the resolver reads it,
/// "add ... to Microsoft To Do" is to create a task, and the tool named <c>create_task</c> does that) comes before the others, whatever words they share
/// (step 110), so that the few tools a small model is given include the one the request needs. No model and no network is involved, and the same
/// request always selects the same.
/// </summary>
internal static partial class LexicalMcpToolSelector
{
    private const int NameWeight = 3;
    private const int ToolNameWeight = 3;
    private const int TitleWeight = 2;
    private const int FallbackToolCount = 3;

    // The words of a request that say nothing about what is asked.
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "your", "can", "please", "with", "from", "that", "this", "what", "whats", "have", "has", "into", "about",
        "its", "are", "was", "were", "will", "would", "could", "should", "any", "all", "not", "but", "how", "who", "when", "where", "why", "just",
        "then", "them", "they", "there", "here", "out", "off", "our", "his", "her", "him", "she", "one", "some", "need", "want", "get", "let", "make",
    };

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotWord();

    // Where a name in camel case starts a new word: a lower-case letter or a digit followed by a capital.
    [GeneratedRegex(@"([\p{Ll}\p{Nd}])(\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();

    /// <summary>The words of <paramref name="text"/> that count: lower case, at least three letters, not a common word, a plural "s" taken off.</summary>
    public static IReadOnlySet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        // A name in camel case or with underscores is read as the words it is made of.
        foreach (var raw in NotWord().Split(CamelBoundary().Replace(text, "$1 $2").ToLowerInvariant()))
        {
            var word = raw.Length > 3 && raw.EndsWith('s') && !raw.EndsWith("ss", StringComparison.Ordinal) ? raw[..^1] : raw;
            if (word.Length >= 3 && !CommonWords.Contains(word))
            {
                words.Add(word);
            }
        }

        return words;
    }

    /// <summary>The selector of apps and tools.</summary>
    public static IMcpToolSelector Instance { get; } = new Selector();

    private sealed class Selector : IMcpToolSelector
    {
        public IReadOnlyList<InstalledIntegration> SelectIntegrations(string request, IReadOnlyList<InstalledIntegration> integrations)
        {
            var requested = Words(request);
            if (requested.Count == 0)
            {
                return [];
            }

            // An app the request asks something of, however the request writes its name ("MS To-Do" for Microsoft To Do: the resolver reads it as the same
            // app), is the one it is about, though no word of its name is in the request (step 110).
            var asked = IntegrationRequestReader.Instance.Read(request, [.. integrations.Select(integration => integration.Name)]);

            // A request that names no app but asks for a kind of thing ("check my calendar", step 116) is about whichever installed app has a tool that does it.
            var needs = asked is null ? [.. CapabilityRequestReader.Read(request), .. ToDoNeeds(request)] : new List<IntegrationNeed>();
            return [.. integrations
                .Select((integration, index) => (
                    Integration: integration,
                    Index: index,
                    Score: Score(requested, integration) + (IsAskedOf(asked, integration) ? NameWeight : 0) + (ServesAny(needs, integration) ? NameWeight : 0)))
                .Where(entry => entry.Score >= 2)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Integration)];
        }

        public IReadOnlyList<McpTool> SelectTools(string request, InstalledIntegration integration, IReadOnlyList<McpTool> tools, int max)
        {
            var requested = Words(request);
            if (requested.Count == 0 || tools.Count == 0 || max <= 0)
            {
                return [];
            }

            var asked = AskedOf(request, integration);
            var named = NamedIn(requested, integration) || asked is not null;

            // What the request asks to be done in the app ("add ... to Microsoft To Do" is to create a task) names the tools that do it, whatever
            // words the request and the tool's name share: a tool named create_task is the one for it even though the request never says create.
            var matches = CapabilityMatches(asked, tools);
            if (asked is null)
            {
                // No app is named, but what is asked for may be a kind of thing (step 116): the tools that do it come first, the first thing asked for first.
                foreach (var need in CapabilityRequestReader.Read(request).Concat(ToDoNeeds(request)))
                {
                    foreach (var name in CapabilityMatcher.Match(
                                 need.Capability, [.. tools.Select(tool => new ToolFacts(tool.Descriptor.Name, tool.Descriptor.Title, tool.Descriptor.Description))]))
                    {
                        matches.TryAdd(name, matches.Count);
                    }
                }
            }

            var scored = tools
                .Select((tool, index) => (
                    Tool: tool,
                    Index: index,
                    Score: Score(requested, tool),
                    Rank: matches.TryGetValue(tool.Descriptor.Name, out var rank) ? rank : int.MaxValue))
                .Where(entry => entry.Score > 0 || entry.Rank != int.MaxValue)
                .OrderBy(entry => entry.Rank)
                .ThenByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Tool)
                .Take(max)
                .ToList();

            // An app that is named and whose tools share no word with the request: its first few are offered, in the order the server lists them.
            return scored.Count == 0 && named ? [.. tools.Take(Math.Min(max, FallbackToolCount))] : scored;
        }

        // "Add homework to my to do", "what's on my to-do list", "tick it off my todo": the user's own list of tasks, written as people say it, two short words
        // that no other reading of the request counts; and "add task ...", whatever follows. It is about whichever connected app has a tool for tasks; with
        // none, nothing is loaded for it. A request that only says "to do" ("what do I have to do") is not: the words must follow "my", "our" or "the".
        private static List<IntegrationNeed> ToDoNeeds(string request)
        {
            var tokens = IntegrationRequestReader.Tokenize(request);
            var found = false;
            for (var at = 0; at + 1 < tokens.Count && !found; at++)
            {
                if (tokens[at] is not ("my" or "our" or "the"))
                {
                    continue;
                }

                found = (tokens[at + 1] == "to" && at + 2 < tokens.Count && tokens[at + 2] == "do") || tokens[at + 1] is "todo" or "todos" or "tasks";
            }

            // "Add task homework due today in ms to due": a task to add, whatever became of the app's name.
            found |= tokens.Any(token => token is "task" or "tasks" or "todo" or "todos") && tokens.Any(token => token is "add" or "create" or "new" or "put" or "make");
            if (!found)
            {
                return [];
            }

            var action = tokens.Any(token => token is "show" or "list" or "what" or "whats" or "which" or "read" or "check" or "see")
                && !tokens.Any(token => token is "add" or "put" or "create")
                ? CapabilityAction.Read
                : tokens.Any(token => token is "tick" or "complete" or "finish" or "done" or "check" && !tokens.Contains("add"))
                    ? CapabilityAction.Complete
                    : CapabilityAction.Create;
            return [new IntegrationNeed("tasks", "tasks", new IntegrationCapability(action, "task"), IntegrationNeedSource.Capability)];
        }

        // What makes an app worth connecting to: its name, or two words of its tools' names, are in the request.
        private static int Score(IReadOnlySet<string> requested, InstalledIntegration integration)
        {
            var score = NamedIn(requested, integration) ? NameWeight : 0;
            var toolWords = integration.Capabilities.ToolNames.SelectMany(name => Words(name)).ToHashSet(StringComparer.Ordinal);
            return score + requested.Count(toolWords.Contains);
        }

        // Whether the tool names the registry kept for the app include one that does what one of the needs asks.
        private static bool ServesAny(IReadOnlyList<IntegrationNeed> needs, InstalledIntegration integration) =>
            needs.Any(need => CapabilityMatcher.Match(need.Capability, [.. integration.Capabilities.ToolNames.Select(name => new ToolFacts(name))]).Count > 0);

        private static bool NamedIn(IReadOnlySet<string> requested, InstalledIntegration integration) =>
            Words(integration.Name).Concat(Words(integration.Id)).Any(requested.Contains);

        // The tools that do what the request asks in this app (step 110), by name and best first (0 is the best), the same way the resolver reads the request
        // and matches tools, so that what it found installed and what is offered agree. Nothing when the request is not an action in this app, and none for
        // deleting: a tool that removes something is never what a request makes more likely to be offered.
        private static Dictionary<string, int> CapabilityMatches(IntegrationNeed? asked, IReadOnlyList<McpTool> tools)
        {
            var matches = new Dictionary<string, int>(StringComparer.Ordinal);
            if (asked is null)
            {
                return matches;
            }

            foreach (var name in CapabilityMatcher.Match(
                         asked.Capability, [.. tools.Select(tool => new ToolFacts(tool.Descriptor.Name, tool.Descriptor.Title, tool.Descriptor.Description))]))
            {
                matches.TryAdd(name, matches.Count);
            }

            return matches;
        }

        // What the request asks of this app, read as the resolver reads it; nothing when it is not an action in this app, and nothing for deleting.
        private static IntegrationNeed? AskedOf(string request, InstalledIntegration integration) =>
            IntegrationRequestReader.Instance.Read(request, [integration.Name]) is { } need && IsAskedOf(need, integration) ? need : null;

        private static bool IsAskedOf(IntegrationNeed? need, InstalledIntegration integration) =>
            need is not null && need.Capability.Action != CapabilityAction.Delete
            && (AppIdentity.IsAbout(integration.Name, need.AppKey) || AppIdentity.KeyOf(integration.Id) == need.AppKey);

        private static int Score(IReadOnlySet<string> requested, McpTool tool)
        {
            var name = Words(tool.Descriptor.Name);
            var title = Words(tool.Descriptor.Title);
            var description = Words(tool.Descriptor.Description);
            return requested.Sum(word => (name.Contains(word) ? ToolNameWeight : 0) + (title.Contains(word) ? TitleWeight : 0) + (description.Contains(word) ? 1 : 0));
        }
    }
}
