using System.Text.RegularExpressions;

namespace Assistant.Tools.Integrations;

/// <summary>What is known of a tool when a capability is matched against it.</summary>
/// <param name="Name">The name the server gives the tool (<c>create_task</c>, <c>addTask</c>, <c>tasks.insert</c>).</param>
/// <param name="Title">Its title, when it has one.</param>
/// <param name="Description">Its description, when it is at hand (the registry keeps only names).</param>
internal readonly record struct ToolFacts(string Name, string? Title = null, string? Description = null);

/// <summary>How much a source says that something supports a capability.</summary>
public enum CapabilityEvidence
{
    /// <summary>Nothing says so, and nothing says it is about the app.</summary>
    None = 0,

    /// <summary>It is about the app, but nothing says whether it can do this.</summary>
    AppOnly = 1,

    /// <summary>Its description or documentation speaks of the action and the kind of thing.</summary>
    Described = 2,

    /// <summary>One of its tools is named for the action and the kind of thing.</summary>
    ToolListed = 3,
}

/// <summary>
/// Decides whether an MCP tool does what a capability asks (PROJECT_SPEC §4.8, step 105) by the words of its name, title and description: fixed lists of
/// the words that mean "create" or "task" in tool names (<c>add</c>, <c>insert</c>, <c>todo</c>), no model and no network. A tool matches when
/// its name or title holds a word for the action and a word for the kind of thing, or its name holds one and its description the other.
/// It is what lets the Assistant tell, from the tool names it kept, that an installed app can do the thing without starting it.
/// </summary>
internal static partial class CapabilityMatcher
{
    private const int StrongScore = 10;
    private const int WeakScore = 5;
    private const int EndsOnObjectBonus = 2;

    private static readonly Dictionary<CapabilityAction, string[]> ActionWords = new()
    {
        [CapabilityAction.Create] = ["create", "add", "new", "insert", "make", "post", "append", "write", "save", "log", "put", "schedule", "book", "store", "record", "capture", "quick", "draft", "remind", "set"],
        [CapabilityAction.Read] = ["get", "list", "read", "fetch", "view", "show", "retrieve", "query", "lookup", "today", "upcoming", "agenda", "inbox", "find", "search", "browse"],
        [CapabilityAction.Search] = ["search", "find", "query", "lookup", "filter", "list", "get"],
        [CapabilityAction.Update] = ["update", "edit", "modify", "change", "rename", "move", "patch", "set", "reschedule", "assign", "label", "tag", "reorder", "snooze", "replace"],
        [CapabilityAction.Complete] = ["complete", "close", "finish", "done", "check", "tick", "resolve", "mark"],
        [CapabilityAction.Delete] = ["delete", "remove", "trash", "destroy", "clear", "archive", "cancel", "erase"],
        [CapabilityAction.Send] = ["send", "post", "reply", "message", "share", "forward", "notify", "publish", "email", "dm", "compose", "draft"],
    };

    // What ends a sentence or a line of documentation.
    private static readonly char[] Separators = ['.', ';', (char)10, (char)13];

    // A tool named for managing a kind of thing does all of it.
    private static readonly string[] GenericActions = ["manage", "handle"];

    // Where a name in camel case starts a new word.
    [GeneratedRegex(@"([\p{Ll}\p{Nd}])(\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();

    /// <summary>The words of <paramref name="text"/>: lower case, a name in camel case, snake case or kebab case taken apart, a plural "s" taken off.</summary>
    public static HashSet<string> Stems(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        foreach (var word in AppIdentity.Words(CamelBoundary().Replace(text, "$1 $2")))
        {
            words.Add(Stem(word));
        }

        return words;
    }

    private static string Stem(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal) ? word[..^1] : word;

    /// <summary>The names of the tools among <paramref name="tools"/> that do what <paramref name="capability"/> asks, best match first.</summary>
    public static IReadOnlyList<string> Match(IntegrationCapability capability, IReadOnlyList<ToolFacts> tools)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(tools);
        return [.. tools
            .Select((tool, index) => (tool.Name, Index: index, Score: Score(capability, tool)))
            .Where(entry => entry.Score >= WeakScore)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Name)];
    }

    /// <summary>
    /// How much <paramref name="names"/> (tool names) and <paramref name="description"/> say that something supports <paramref name="capability"/>:
    /// a tool named for it is <see cref="CapabilityEvidence.ToolListed"/>, a description of the action and the kind of thing is
    /// <see cref="CapabilityEvidence.Described"/>, and anything else is <see cref="CapabilityEvidence.AppOnly"/> (the caller has already decided it is about the app).
    /// </summary>
    public static CapabilityEvidence EvidenceOf(IntegrationCapability capability, IReadOnlyList<string> names, string? description)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(names);
        if (Match(capability, [.. names.Select(name => new ToolFacts(name))]).Count > 0)
        {
            return CapabilityEvidence.ToolListed;
        }

        var words = Stems(description);
        return HasAction(capability, words) && HasObject(capability, words) ? CapabilityEvidence.Described : CapabilityEvidence.AppOnly;
    }

    /// <summary>
    /// Whether some sentence or line of <paramref name="text"/> speaks of the action and the kind of thing together ("Create a task in the list"). It is how
    /// a README is read for support of a capability when it does not list its tools.
    /// </summary>
    public static bool Mentions(IntegrationCapability capability, string? text)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var sentence in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var words = Stems(sentence);
            if (HasAction(capability, words) && HasObject(capability, words))
            {
                return true;
            }
        }

        return false;
    }

    private static int Score(IntegrationCapability capability, ToolFacts tool)
    {
        var named = Stems(tool.Name);
        named.UnionWith(Stems(tool.Title));
        var described = Stems(tool.Description);

        var actionInName = HasAction(capability, named);
        var objectInName = HasObject(capability, named);
        if (actionInName && objectInName)
        {
            // "create_task" does what was asked; "create_task_list" does something to the list the task is in, so a name that ends on the thing is better.
            return StrongScore + (EndsOnObject(capability, tool.Name) ? EndsOnObjectBonus : 0);
        }

        // Named for one and described as the other ("todoist_quick_add: Add a task using natural language").
        if (actionInName && HasObject(capability, described) || objectInName && HasAction(capability, described))
        {
            return WeakScore;
        }

        return 0;
    }

    // Whether the last word of a tool's name is the kind of thing the capability is about.
    private static bool EndsOnObject(IntegrationCapability capability, string name)
    {
        if (capability.Object is null)
        {
            return false;
        }

        var last = AppIdentity.Words(CamelBoundary().Replace(name, "$1 $2")).LastOrDefault();
        return last is not null && CapabilityObjects.WordsFor(capability.Object).Any(word => Stem(word) == Stem(last));
    }

    private static bool HasAction(IntegrationCapability capability, IReadOnlySet<string> words) =>
        ActionWords[capability.Action].Any(words.Contains) || GenericActions.Any(words.Contains);

    private static bool HasObject(IntegrationCapability capability, IReadOnlySet<string> words) =>
        capability.Object is null || CapabilityObjects.WordsFor(capability.Object).Any(word => words.Contains(Stem(word)));
}
