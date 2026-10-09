using System.Text;

namespace Assistant.Tools.Integrations;

/// <summary>
/// An app the Assistant knows by name (PROJECT_SPEC §4.8, step 105), so that "add milk to Todoist" is recognised without a model and the finder
/// knows whose repositories to trust first.
/// </summary>
/// <param name="Key">The app as a key (<see cref="AppIdentity.Squash"/> of its name).</param>
/// <param name="Name">The name the user is shown.</param>
/// <param name="Aliases">Other ways people write it (lower case words; the name itself is always one).</param>
/// <param name="DefaultObject">What a request for it is usually about ("task" for a to-do app), when the request does not say.</param>
/// <param name="Owners">
/// The GitHub accounts and npm scopes that belong to the app's maker. A repository or package owned by one is the maker's; it is the only way
/// a candidate is called <c>VerifiedVendor</c>, so each entry is one whose owner is known for certain.
/// </param>
/// <param name="Domains">The maker's domains, as the official MCP registry's reverse-DNS names show them (<c>com.microsoft</c> is microsoft.com).</param>
/// <param name="SearchTerm">The one word the registries are searched with, when the key is not it.</param>
/// <param name="NeedsContext">
/// Whether the name is also an ordinary word (Linear, Teams): a request counts as being about the app only when the name comes after "to", "in",
/// "on" and the like, or is followed by a word such as "app" or "issue".
/// </param>
public sealed record KnownApp(
    string Key,
    string Name,
    IReadOnlyList<string> Aliases,
    string? DefaultObject,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Domains,
    string? SearchTerm = null,
    bool NeedsContext = false);

/// <summary>
/// The apps the Assistant knows by name. It is a fixed list in code: an app that is not here is still found when it is installed or when the
/// request calls it an app, but nothing about it is trusted in advance. Apps that keep files (OneDrive, Dropbox) are left out on purpose:
/// "find it in OneDrive" is a file search on this PC, not a connected app.
/// </summary>
public static class KnownApps
{
    private static readonly KnownApp[] Apps =
    [
        new("microsofttodo", "Microsoft To Do", ["microsoft to do", "microsoft todo", "ms to do", "ms todo", "mstodo", "microsoft to-do"], "task",
            ["microsoft", "microsoftgraph"], ["microsoft.com"], "todo"),
        new("todoist", "Todoist", [], "task", ["doist"], ["todoist.com", "doist.com"]),
        new("ticktick", "TickTick", ["tick tick"], "task", [], ["ticktick.com"]),
        new("googletasks", "Google Tasks", [], "task", [], []),
        new("asana", "Asana", [], "task", ["asana"], ["asana.com"]),
        new("clickup", "ClickUp", ["click up"], "task", [], ["clickup.com"]),
        new("trello", "Trello", [], "card", ["trello"], ["trello.com"]),
        new("notion", "Notion", [], "page", ["makenotion", "notionhq"], ["notion.so", "notion.com"]),
        new("evernote", "Evernote", [], "note", ["evernote"], ["evernote.com"]),
        new("onenote", "OneNote", ["microsoft onenote", "one note"], "note", ["microsoft"], ["microsoft.com"]),
        new("obsidian", "Obsidian", [], "note", ["obsidianmd"], ["obsidian.md"]),
        new("confluence", "Confluence", ["atlassian confluence"], "page", ["atlassian"], ["atlassian.com"]),
        new("jira", "Jira", ["atlassian jira"], "issue", ["atlassian"], ["atlassian.com"]),
        new("linear", "Linear", ["linear app"], "issue", ["linear"], ["linear.app"], NeedsContext: true),
        new("github", "GitHub", ["git hub"], "issue", ["github"], ["github.com"]),
        new("gitlab", "GitLab", ["git lab"], "issue", [], ["gitlab.com"]),
        new("bitbucket", "Bitbucket", [], "pull request", ["atlassian"], ["atlassian.com"]),
        new("sentry", "Sentry", [], "issue", ["getsentry"], ["sentry.io"]),
        new("slack", "Slack", [], "message", ["slackapi", "slack"], ["slack.com"]),
        new("discord", "Discord", [], "message", ["discord"], ["discord.com"]),
        new("microsoftteams", "Microsoft Teams", ["ms teams", "teams app"], "message", ["microsoft"], ["microsoft.com"]),
        new("telegram", "Telegram", [], "message", [], ["telegram.org"]),
        new("whatsapp", "WhatsApp", ["whats app"], "message", [], ["whatsapp.com"]),
        new("beeper", "Beeper", ["beeper desktop", "beeper app"], "message", ["beeper"], ["beeper.com"]),
        new("gmail", "Gmail", ["google mail"], "message", [], []),
        new("outlook", "Outlook", ["microsoft outlook", "outlook mail"], "message", ["microsoft"], ["microsoft.com"], NeedsContext: true),
        new("googlecalendar", "Google Calendar", ["gcal", "google cal"], "event", [], []),
        new("calendly", "Calendly", [], "event", [], ["calendly.com"]),
        new("zoom", "Zoom", [], "event", [], ["zoom.us"], NeedsContext: true),
        new("spotify", "Spotify", [], "track", ["spotify"], ["spotify.com"]),
        new("airtable", "Airtable", [], "record", ["airtable"], ["airtable.com"]),
        new("hubspot", "HubSpot", ["hub spot"], "contact", ["hubspot"], ["hubspot.com"]),
        new("salesforce", "Salesforce", [], "record", ["salesforce"], ["salesforce.com"]),
        new("raindrop", "Raindrop.io", ["raindrop io", "raindrop"], "bookmark", [], ["raindrop.io"]),
        new("figma", "Figma", [], null, ["figma"], ["figma.com"]),
        new("stripe", "Stripe", [], null, ["stripe"], ["stripe.com"]),
        new("homeassistant", "Home Assistant", ["hass"], null, ["home-assistant"], ["home-assistant.io"]),
    ];

    private static readonly Dictionary<string, KnownApp> ByKey = Apps.ToDictionary(app => app.Key, StringComparer.Ordinal);

    // Every way of writing an app's name, squashed, to the app.
    private static readonly Dictionary<string, KnownApp> ByAlias = BuildAliasIndex();

    /// <summary>Every app, in the order they are listed here.</summary>
    public static IReadOnlyList<KnownApp> All => Apps;

    /// <summary>The app with this key, or <see langword="null"/>.</summary>
    // A connection through Pipedream ("microsofttodopipedream", "discordpipedream") is the same app as the one it reaches.
    public static KnownApp? ByAppKey(string key) =>
        ByKey.GetValueOrDefault(key.EndsWith(PipedreamSuffix, StringComparison.Ordinal) && key.Length > PipedreamSuffix.Length ? key[..^PipedreamSuffix.Length] : key);

    private const string PipedreamSuffix = "pipedream";

    /// <summary>The app that <paramref name="name"/> names (any way of writing it), or <see langword="null"/>.</summary>
    public static KnownApp? Find(string? name) => name is null ? null : ByAlias.GetValueOrDefault(AppIdentity.Squash(name));

    private static Dictionary<string, KnownApp> BuildAliasIndex()
    {
        var index = new Dictionary<string, KnownApp>(StringComparer.Ordinal);
        foreach (var app in Apps)
        {
            foreach (var alias in app.Aliases.Prepend(app.Name))
            {
                // The first app to claim a way of writing a name keeps it.
                index.TryAdd(AppIdentity.Squash(alias), app);
            }
        }

        return index;
    }
}

/// <summary>
/// An app as a key (PROJECT_SPEC §4.8, step 105): the same for "Microsoft To Do", "microsoft todo", "MS To-Do" and an installed integration called
/// "Microsoft To Do MCP". It is how a request, an installed integration, a server configured in another program and a candidate found on the web
/// are told to be about one app.
/// </summary>
public static class AppIdentity
{
    // Words that say what a thing is and not which app it is.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "mcp", "server", "servers", "integration", "connector", "official", "app", "plugin", "tool", "tools", "the", "for", "by", "unofficial", "community", "api",
    };

    /// <summary>
    /// The lower-case letters and digits of <paramref name="text"/> with the words that do not name an app taken out, run together
    /// ("Microsoft To-Do MCP Server" is <c>microsofttodo</c>).
    /// </summary>
    public static string Squash(string? text)
    {
        var builder = new StringBuilder();
        foreach (var word in Words(text))
        {
            if (!NoiseWords.Contains(word))
            {
                builder.Append(word);
            }
        }

        return builder.ToString();
    }

    /// <summary>The key of the app named <paramref name="name"/>: the known app's key when it is one, otherwise <see cref="Squash"/>.</summary>
    public static string KeyOf(string? name) => KnownApps.Find(name)?.Key ?? Squash(name);

    /// <summary>
    /// Whether <paramref name="text"/> is about the app with <paramref name="appKey"/>: its key is the whole text's key, or it holds the key (a name of
    /// at least five characters, so that <c>mcp-microsoft-todo-server</c> is about Microsoft To Do and <c>todo-list</c> is not).
    /// </summary>
    public static bool IsAbout(string? text, string appKey)
    {
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var key = KeyOf(text);
        if (key == appKey)
        {
            return true;
        }

        // A short name (Jira) counts only as a word of its own, so that "digit" is not about Git.
        if (Words(text).Contains(appKey, StringComparer.Ordinal))
        {
            return true;
        }

        var app = KnownApps.ByAppKey(appKey);
        var keys = app is null ? [appKey] : app.Aliases.Prepend(app.Name).Select(Squash).Append(appKey).Distinct(StringComparer.Ordinal).ToArray();
        var squashed = Squash(text);
        return keys.Any(candidate => candidate.Length >= 5 && squashed.Contains(candidate, StringComparison.Ordinal));
    }

    /// <summary>The lower-case words (runs of letters and digits) of <paramref name="text"/>.</summary>
    public static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var word = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }
}
