namespace Assistant.Tools.Integrations;

/// <summary>What a request wants done in an external app.</summary>
public enum CapabilityAction
{
    /// <summary>Make something new: add a task, create an event, write a note, log an issue.</summary>
    Create = 0,

    /// <summary>Look at something: list the tasks, show the events of today.</summary>
    Read = 1,

    /// <summary>Look for something by what it says.</summary>
    Search = 2,

    /// <summary>Change something that exists.</summary>
    Update = 3,

    /// <summary>Finish something: tick a task off, close an issue.</summary>
    Complete = 4,

    /// <summary>Remove something. The Assistant refuses to in a connected app (PROJECT_SPEC §4.8).</summary>
    Delete = 5,

    /// <summary>Send something to someone.</summary>
    Send = 6,
}

/// <summary>
/// What a request wants done in an external app: an action on a kind of thing ("create" a "task"). The thing is one word of a fixed list
/// (<see cref="CapabilityObjects.Objects"/>), or <see langword="null"/> when the request does not say and the app has no usual one.
/// Nothing the user wrote beyond these two words is kept in it, so it can be shown, compared and searched with.
/// </summary>
/// <param name="Action">What is wanted done.</param>
/// <param name="Object">The kind of thing, such as <c>task</c> or <c>event</c>.</param>
public sealed record IntegrationCapability(CapabilityAction Action, string? Object)
{
    /// <summary>The action as a word.</summary>
    public string Verb => Action switch
    {
        CapabilityAction.Create => "create",
        CapabilityAction.Read => "read",
        CapabilityAction.Search => "search",
        CapabilityAction.Update => "update",
        CapabilityAction.Complete => "complete",
        CapabilityAction.Delete => "delete",
        CapabilityAction.Send => "send",
        _ => "use",
    };

    /// <summary>The capability in two words, such as <c>create task</c>.</summary>
    public string Phrase => Object is null ? Verb : Verb + " " + Object;

    /// <summary>The capability as a key for a cache.</summary>
    public string Key => Verb + ":" + (Object ?? string.Empty);
}

/// <summary>Where the Assistant learned that a request is about an app.</summary>
public enum IntegrationNeedSource
{
    /// <summary>The app is one the Assistant knows by name (<see cref="KnownApps"/>).</summary>
    Catalog = 0,

    /// <summary>The app is installed (its name is in the registry).</summary>
    Installed = 1,

    /// <summary>The request called it an app, an integration or a service by a name the Assistant does not know.</summary>
    Stated = 2,

    /// <summary>
    /// The request named no app at all, only a thing the user has ("my calendar") or someone to tell (step 116): the need is for any integration that can do it
    /// (<see cref="IntegrationNeed.ForCalendar"/>, <see cref="IntegrationNeed.ForMessaging"/>), and its "app" is the kind of thing, such as <c>calendar</c>.
    /// </summary>
    Capability = 3,
}

/// <summary>
/// An external app and what a request wants done in it (PROJECT_SPEC §4.8, step 105), read from the request by fixed rules (no model, no
/// network). It is the question the resolver answers and the only thing the Integration Finder searches with: the app's name and the two words of the
/// capability, never the request, so what the user wanted added ("buy milk") never leaves the PC.
/// </summary>
/// <param name="AppName">The app's name as shown to the user, such as <c>Microsoft To Do</c>.</param>
/// <param name="AppKey">The app as a key (<see cref="AppIdentity"/>): lower-case letters and digits, the same for every way of writing its name.</param>
/// <param name="Capability">What is wanted done in it.</param>
/// <param name="Source">Where the app was recognised.</param>
public sealed record IntegrationNeed(string AppName, string AppKey, IntegrationCapability Capability, IntegrationNeedSource Source)
{
    /// <summary>The need as a key for a cache.</summary>
    public string CacheKey => AppKey + "|" + Capability.Key;

    /// <summary>The need to read the user's calendar, whatever app it is in (step 116).</summary>
    public static IntegrationNeed ForCalendar() =>
        new("calendar", "calendar", new IntegrationCapability(CapabilityAction.Read, "event"), IntegrationNeedSource.Capability);

    /// <summary>The need to send a message to someone, whatever messaging app it is through (step 116).</summary>
    public static IntegrationNeed ForMessaging() =>
        new("messaging", "messaging", new IntegrationCapability(CapabilityAction.Send, "message"), IntegrationNeedSource.Capability);

    /// <summary>Whether the need is for a kind of thing and not for a particular app (<see cref="IntegrationNeedSource.Capability"/>).</summary>
    public bool IsForAnyApp => Source == IntegrationNeedSource.Capability;
}

/// <summary>The kinds of thing a capability can act on, and the words a tool or a request uses for each.</summary>
public static class CapabilityObjects
{
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["task"] = ["task", "todo", "reminder", "chore"],
        ["event"] = ["event", "meeting", "appointment", "calendar", "invite"],
        ["note"] = ["note", "memo", "page", "doc", "document", "entry", "block"],
        ["page"] = ["page", "note", "doc", "document", "wiki", "entry", "block"],
        ["message"] = ["message", "email", "mail", "dm", "chat", "thread", "reply", "text"],
        ["issue"] = ["issue", "ticket", "bug", "story"],
        ["pull request"] = ["pr", "pull", "merge"],
        ["card"] = ["card", "board"],
        ["file"] = ["file", "folder", "attachment"],
        ["track"] = ["track", "song", "music", "album"],
        ["playlist"] = ["playlist", "queue"],
        ["channel"] = ["channel", "room"],
        ["contact"] = ["contact", "person", "people", "lead"],
        ["project"] = ["project", "workspace"],
        ["comment"] = ["comment"],
        ["record"] = ["record", "row", "database", "table", "entry"],
        ["bookmark"] = ["bookmark", "link", "url"],
    };

    /// <summary>The kinds of thing a capability can act on, as the words a person uses for them.</summary>
    public static IReadOnlyCollection<string> Objects => Synonyms.Keys;

    /// <summary>The words that mean <paramref name="obj"/> in a tool's name or description (lower case, singular), or just the word itself when it is not a kind in the list.</summary>
    public static IReadOnlyList<string> WordsFor(string obj) => Synonyms.TryGetValue(obj, out var words) ? words : [obj];
}
