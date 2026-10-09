using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Tools.Integrations;

/// <summary>Reads a request for the app it is about and what it wants done there (PROJECT_SPEC §4.8, step 105).</summary>
public interface IIntegrationRequestReader
{
    /// <summary>
    /// The app and the capability <paramref name="request"/> asks for, or <see langword="null"/> when it is not a request to do something in an
    /// external app that can be told with certainty.
    /// </summary>
    /// <param name="request">What the user wrote. It is private content: it is read, never kept.</param>
    /// <param name="installedNames">The names of the installed integrations, which count as apps too.</param>
    IntegrationNeed? Read(string? request, IReadOnlyCollection<string>? installedNames = null);
}

/// <summary>
/// The deterministic reader of requests for external apps (PROJECT_SPEC §4.8, step 105): "Add 'buy milk' to Microsoft To Do" is the app Microsoft To Do and the
/// capability <i>create task</i>. It uses fixed word lists and no model, no network and no state, so the same request always reads the same way
/// and a request that does not name an app costs nothing. It errs on the side of reading nothing: a question ("how do I add a task in Todoist"), a
/// request with no action word, a name that is also an ordinary word with nothing around it to say it is an app, and everything the Assistant's own
/// tools do (opening, launching, playing) are not requests for an external app. The capability it returns holds two fixed words; nothing the
/// request said beyond them (the text of the task, a person's name) is kept.
/// </summary>
public sealed partial class IntegrationRequestReader : IIntegrationRequestReader
{
    /// <summary>The longest request that is read; a longer one is a pasted text and not a request for an app.</summary>
    public const int MaxRequestLength = 400;

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "hey", "hi", "hello", "ok", "okay", "please", "pls", "assistant", "so", "now", "just", "also", "then", "and", "kindly", "quickly",
    };

    // Words that start a question about an app or about the Assistant, not a request to do something in the app.
    private static readonly HashSet<string> Questions = new(StringComparer.Ordinal)
    {
        "how", "why", "where", "when", "which", "who", "whom", "whose", "is", "are", "was", "were", "does", "do", "did", "should", "shall",
        "explain", "describe", "define", "compare", "can", "could", "would", "will", "am", "has", "have", "had", "isnt", "arent",
    };

    // The words before a name that is also an ordinary word, or after it, that say it is an app.
    private static readonly HashSet<string> ContextBefore = new(StringComparer.Ordinal)
    {
        "to", "in", "on", "into", "onto", "from", "via", "using", "with", "at", "for", "my", "your", "our", "the",
    };

    private static readonly HashSet<string> ContextAfter = new(StringComparer.Ordinal)
    {
        "app", "issue", "issues", "ticket", "tickets", "task", "tasks", "board", "workspace", "project", "projects", "account", "integration", "channel", "meeting",
    };

    // After an app's name, words for a thing inside the app: "create a Notion page" is a request for Notion even though a verb stands before the name.
    private static readonly HashSet<string> ThingAfter = new(StringComparer.Ordinal)
    {
        "page", "pages", "task", "tasks", "issue", "issues", "ticket", "tickets", "board", "card", "cards", "message", "messages", "event", "events", "note", "notes",
        "doc", "docs", "document", "list", "reminder", "reminders", "channel", "post", "comment", "comments", "file", "files", "project", "projects", "playlist",
        "track", "song", "email", "emails", "meeting", "invite", "bookmark", "record", "records", "row", "rows", "contact", "contacts", "calendar",
    };

    // Words that act on an app itself and not on what is in it ("open Slack", "install Notion"); they and the verbs of Verbs make the app the thing acted on.
    private static readonly HashSet<string> AppVerbs = new(StringComparer.Ordinal)
    {
        "open", "launch", "start", "run", "install", "uninstall", "download", "restart", "quit", "exit", "kill", "reinstall", "upgrade", "enable", "disable", "pin", "unpin",
    };

    private static readonly HashSet<string> Determiners = new(StringComparer.Ordinal) { "the", "a", "an", "my", "your", "our", "this", "that", "new" };

    // Ways of asking that are followed by the request itself.
    private static readonly string[][] Prefixes =
    [
        ["can", "you"], ["could", "you"], ["would", "you"], ["will", "you"], ["can", "u"], ["go", "ahead", "and"], ["i", "want", "to"], ["i", "need", "to"],
        ["i", "would", "like", "to"], ["id", "like", "to"], ["i", "wanna"], ["wanna"], ["lets"], ["let", "us"], ["help", "me"],
    ];

    private static readonly Dictionary<string, CapabilityAction> Verbs = new(StringComparer.Ordinal)
    {
        ["add"] = CapabilityAction.Create, ["create"] = CapabilityAction.Create, ["make"] = CapabilityAction.Create, ["put"] = CapabilityAction.Create,
        ["remind"] = CapabilityAction.Create, ["schedule"] = CapabilityAction.Create, ["log"] = CapabilityAction.Create, ["record"] = CapabilityAction.Create,
        ["save"] = CapabilityAction.Create, ["write"] = CapabilityAction.Create, ["insert"] = CapabilityAction.Create, ["append"] = CapabilityAction.Create,
        ["jot"] = CapabilityAction.Create, ["book"] = CapabilityAction.Create, ["draft"] = CapabilityAction.Create, ["set"] = CapabilityAction.Create,
        ["capture"] = CapabilityAction.Create,
        ["show"] = CapabilityAction.Read, ["list"] = CapabilityAction.Read, ["read"] = CapabilityAction.Read, ["get"] = CapabilityAction.Read,
        ["fetch"] = CapabilityAction.Read, ["view"] = CapabilityAction.Read, ["display"] = CapabilityAction.Read, ["check"] = CapabilityAction.Read,
        ["see"] = CapabilityAction.Read, ["give"] = CapabilityAction.Read,
        ["find"] = CapabilityAction.Search, ["search"] = CapabilityAction.Search, ["locate"] = CapabilityAction.Search, ["lookup"] = CapabilityAction.Search,
        ["update"] = CapabilityAction.Update, ["edit"] = CapabilityAction.Update, ["change"] = CapabilityAction.Update, ["rename"] = CapabilityAction.Update,
        ["move"] = CapabilityAction.Update, ["reschedule"] = CapabilityAction.Update, ["modify"] = CapabilityAction.Update, ["assign"] = CapabilityAction.Update,
        ["tag"] = CapabilityAction.Update, ["label"] = CapabilityAction.Update, ["mark"] = CapabilityAction.Update, ["snooze"] = CapabilityAction.Update,
        ["complete"] = CapabilityAction.Complete, ["finish"] = CapabilityAction.Complete, ["tick"] = CapabilityAction.Complete, ["resolve"] = CapabilityAction.Complete,
        ["close"] = CapabilityAction.Complete,
        ["delete"] = CapabilityAction.Delete, ["remove"] = CapabilityAction.Delete, ["cancel"] = CapabilityAction.Delete, ["clear"] = CapabilityAction.Delete,
        ["archive"] = CapabilityAction.Delete, ["trash"] = CapabilityAction.Delete, ["erase"] = CapabilityAction.Delete,
        ["send"] = CapabilityAction.Send, ["share"] = CapabilityAction.Send, ["message"] = CapabilityAction.Send, ["email"] = CapabilityAction.Send,
        ["text"] = CapabilityAction.Send, ["dm"] = CapabilityAction.Send, ["forward"] = CapabilityAction.Send, ["reply"] = CapabilityAction.Send,
        ["notify"] = CapabilityAction.Send, ["ping"] = CapabilityAction.Send, ["tell"] = CapabilityAction.Send, ["post"] = CapabilityAction.Send,
    };

    // Verbs that are only a request for an app with a thing named after them ("close the issue", but not "close Spotify").
    private static readonly HashSet<string> NeedObject = new(StringComparer.Ordinal) { "close", "set", "check", "see", "make", "give", "clear" };

    private static readonly Dictionary<string, string> ObjectWords = BuildObjectWords();

    // A name the request gives an app with "app", "integration" and the like after it.
    [GeneratedRegex(
        @"\b(?:in|on|to|into|using|with|via|from)\s+(?:the\s+|my\s+|your\s+)?(?<name>[\p{Lu}][\p{L}\p{Nd}.+&-]*(?:\s+[\p{Lu}][\p{L}\p{Nd}.+&-]*){0,2})\s+(?:app|integration|service|workspace|account)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex StatedApp();

    private static readonly HashSet<string> NotAppNames = new(StringComparer.Ordinal)
    {
        "windows", "start", "settings", "this", "that", "these", "those", "my", "the", "your", "our", "his", "her", "their", "its", "a", "an", "some",
        "default", "main", "new", "old", "work", "personal", "user", "admin", "system", "file", "files",
    };

    /// <summary>The reader.</summary>
    public static IntegrationRequestReader Instance { get; } = new();

    /// <inheritdoc/>
    public IntegrationNeed? Read(string? request, IReadOnlyCollection<string>? installedNames = null)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Length > MaxRequestLength)
        {
            return null;
        }

        var tokens = Tokenize(request);
        if (tokens.Count == 0 || !IsRequest(tokens, out var body))
        {
            return null;
        }

        var app = FindApp(request, tokens, body, installedNames) ?? FindStatedApp(request);
        return app is not null && ReadCapability(tokens, body, app) is { } capability
            ? new IntegrationNeed(app.Name, app.Key, capability, app.Source)
            : null;
    }

    // Lower-case words, with apostrophes taken out so that "what's" is one word.
    internal static List<string> Tokenize(string request)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        foreach (var character in request)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (character is '\'' or '’' or '`')
            {
                // "what's" is read as "whats".
            }
            else if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
        }

        return tokens;
    }

    // Whether the request is a request and not a question about an app; body is where the request proper starts, after "please", "can you" and the like.
    internal static bool IsRequest(List<string> tokens, out int body)
    {
        var at = 0;
        while (at < tokens.Count)
        {
            if (Fillers.Contains(tokens[at]))
            {
                at++;
                continue;
            }

            var prefix = FindPrefix(tokens, at);
            if (prefix is null)
            {
                break;
            }

            at += prefix.Length;
        }

        body = at;

        if (body >= tokens.Count)
        {
            return false;
        }

        var first = tokens[body];
        if (first is "what" or "whats")
        {
            // "What's on my Google Calendar?" asks for something of the user's own; "what is Todoist" does not.
            return tokens.Contains("my") || tokens.Contains("our");
        }

        return !Questions.Contains(first);
    }

    private static string[]? FindPrefix(List<string> tokens, int at)
    {
        foreach (var prefix in Prefixes)
        {
            if (Has(tokens, at, prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private static bool Has(List<string> tokens, int at, params string[] words)
    {
        if (at + words.Length > tokens.Count)
        {
            return false;
        }

        for (var i = 0; i < words.Length; i++)
        {
            if (tokens[at + i] != words[i])
            {
                return false;
            }
        }

        return true;
    }

    private static App? FindApp(string request, List<string> tokens, int body, IReadOnlyCollection<string>? installedNames)
    {
        var entries = new List<(string[] Words, App App, bool NeedsContext)>();
        var installedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in installedNames ?? [])
        {
            var key = AppIdentity.KeyOf(name);
            if (key.Length == 0 || !installedKeys.Add(key))
            {
                continue;
            }

            var known = KnownApps.ByAppKey(key);
            var words = AppIdentity.Words(name).Where(word => AppIdentity.Squash(word).Length > 0).ToArray();
            if (words.Length > 0)
            {
                entries.Add((words, new App(known?.Name ?? Clean(name), key, known?.DefaultObject, IntegrationNeedSource.Installed), false));
            }
        }

        foreach (var known in KnownApps.All)
        {
            // An app that is installed is read as installed, whatever else is known about it, and without the care a common word needs.
            var source = installedKeys.Contains(known.Key) ? IntegrationNeedSource.Installed : IntegrationNeedSource.Catalog;
            var needsContext = known.NeedsContext && source == IntegrationNeedSource.Catalog;
            foreach (var alias in known.Aliases.Prepend(known.Name))
            {
                var words = AppIdentity.Words(alias).ToArray();
                if (words.Length > 0)
                {
                    entries.Add((words, new App(known.Name, known.Key, known.DefaultObject, source), needsContext));
                }
            }
        }

        // The longest name first, so that "microsoft teams" is not read as the shorter name inside it.
        foreach (var entry in entries.OrderByDescending(entry => entry.Words.Length))
        {
            for (var at = body; at + entry.Words.Length <= tokens.Count; at++)
            {
                if (!Has(tokens, at, entry.Words))
                {
                    continue;
                }

                if (!IsTargetApp(request, tokens, at, entry.Words, entry.NeedsContext))
                {
                    continue;
                }

                return entry.App with { Start = at, Length = entry.Words.Length };
            }
        }

        return null;
    }

    // Whether the app named at tokens[at..] is where the request wants something done, and not the thing it acts on ("add Slack to my startup")
    // or an ordinary word that happens to be an app's name ("the linear algebra course").
    private static bool IsTargetApp(string request, List<string> tokens, int at, string[] words, bool needsContext)
    {
        var next = at + words.Length < tokens.Count ? tokens[at + words.Length] : null;

        // Before the name, past "the", "a" and "my", stands the word that decides: a verb makes the app what is acted on, unless a thing inside the app follows.
        var before = at - 1;
        while (before >= 0 && Determiners.Contains(tokens[before]))
        {
            before--;
        }

        if (before >= 0 && (Verbs.ContainsKey(tokens[before]) || AppVerbs.Contains(tokens[before])) && (next is null || !ThingAfter.Contains(next)))
        {
            return false;
        }

        if (!needsContext)
        {
            return true;
        }

        // A name that is also an ordinary word counts only when it is written as a name and something around it says it is an app.
        var hasContext = at > 0 && ContextBefore.Contains(tokens[at - 1]) || next is not null && (ContextAfter.Contains(next) || ThingAfter.Contains(next));
        return hasContext && IsWrittenAsName(request, words);
    }

    // Whether the request writes the words with a capital letter ("Linear", not "linear").
    private static bool IsWrittenAsName(string request, string[] words)
    {
        var pattern = string.Join(@"[^\p{L}\p{Nd}]+", words.Select(Regex.Escape));
        return Regex.Matches(request, @"(?<![\p{L}\p{Nd}])" + pattern + @"(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(match => char.IsUpper(match.Value[0]));
    }

    // A name the request itself calls an app ("in the Fooble app"): not known, but the user said what it is.
    private static App? FindStatedApp(string request)
    {
        foreach (Match match in StatedApp().Matches(request))
        {
            var name = Clean(match.Groups["name"].Value);
            var words = AppIdentity.Words(name).ToArray();
            if (words.Length == 0 || name.Length is < 2 or > 40 || NotAppNames.Contains(words[0]) || AppIdentity.Squash(name).Length < 2)
            {
                continue;
            }

            return new App(name, AppIdentity.KeyOf(name), null, IntegrationNeedSource.Stated);
        }

        return null;
    }

    private static string Clean(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name.Trim())
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static IntegrationCapability? ReadCapability(List<string> tokens, int body, App app)
    {
        bool inApp(int at) => at >= app.Start && at < app.Start + app.Length;

        // The first action word that is not part of the app's name.
        var verbAt = -1;
        var action = default(CapabilityAction);
        for (var at = body; at < tokens.Count; at++)
        {
            if (inApp(at) || !Verbs.TryGetValue(tokens[at], out action))
            {
                continue;
            }

            verbAt = at;
            break;
        }

        // "What's on my Google Calendar?" has no action word: asking what is in the app is asking to read it.
        if (verbAt < 0 && body < tokens.Count && tokens[body] is "what" or "whats")
        {
            action = CapabilityAction.Read;
            verbAt = body;
        }

        if (verbAt < 0)
        {
            return null;
        }

        var verb = tokens[verbAt];
        if (verb == "check" && verbAt + 1 < tokens.Count && tokens[verbAt + 1] == "off")
        {
            action = CapabilityAction.Complete;
        }
        else if (verb == "mark" && tokens.Skip(verbAt + 1).Any(word => word is "done" or "complete" or "completed" or "finished"))
        {
            action = CapabilityAction.Complete;
        }

        string? obj = null;
        for (var at = body; at < tokens.Count && obj is null; at++)
        {
            if (at == verbAt || inApp(at))
            {
                continue;
            }

            if (tokens[at] == "pull" && at + 1 < tokens.Count && tokens[at + 1] == "request")
            {
                obj = "pull request";
            }
            else if (ObjectWords.TryGetValue(tokens[at], out var found))
            {
                obj = found;
            }
        }

        // A verb such as "close" is a request for an app only when it says what is closed.
        if (NeedObject.Contains(verb) && obj is null)
        {
            return null;
        }

        return new IntegrationCapability(action, obj ?? app.DefaultObject);
    }

    private static Dictionary<string, string> BuildObjectWords()
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string obj, params string[] forms)
        {
            foreach (var form in forms)
            {
                words[form] = obj;
            }
        }

        Add("task", "task", "tasks", "todo", "todos", "reminder", "reminders", "chore", "chores");
        Add("event", "event", "events", "meeting", "meetings", "appointment", "appointments");
        Add("note", "note", "notes", "memo", "memos");
        Add("page", "page", "pages");
        Add("message", "message", "messages", "dm", "dms", "email", "emails", "mail");
        Add("issue", "issue", "issues", "ticket", "tickets", "bug", "bugs");
        Add("pull request", "pr", "prs");
        Add("card", "card", "cards");
        Add("file", "file", "files", "folder", "folders");
        Add("track", "song", "songs", "track", "tracks", "album", "albums");
        Add("playlist", "playlist", "playlists");
        Add("channel", "channel", "channels");
        Add("contact", "contact", "contacts");
        Add("project", "project", "projects");
        Add("comment", "comment", "comments");
        Add("record", "row", "rows", "record", "records");
        Add("bookmark", "bookmark", "bookmarks");
        return words;
    }

    // The app a request is about, and where its name is in the request.
    private sealed record App(string Name, string Key, string? DefaultObject, IntegrationNeedSource Source)
    {
        public int Start { get; init; } = -1;

        public int Length { get; init; }
    }
}
