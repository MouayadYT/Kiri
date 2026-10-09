namespace Assistant.Core.Settings;

/// <summary>
/// Cleanup (Settings > Privacy, and setup): chats that are deleted by themselves once they have not been added to for a while. A chat had in the
/// Search or Ask bar is usually a quick question, and one had in the full window is usually kept for longer, so each has its own time. Both are
/// off until the user turns them on: nothing of theirs is deleted that they did not ask to have deleted.
/// </summary>
public sealed record CleanupSettings
{
    /// <summary>The time a chat from the bar is kept for when nothing else was chosen, in hours.</summary>
    public const int DefaultBarChatHours = 10;

    /// <summary>The time a chat from the full window is kept for when nothing else was chosen, in days.</summary>
    public const int DefaultWindowChatDays = 7;

    /// <summary>Whether chats had in the Search or Ask bar are deleted after <see cref="BarChatHours"/>.</summary>
    public bool DeleteBarChats { get; init; }

    /// <summary>How many hours after it was last added to a chat from the bar is deleted.</summary>
    public int BarChatHours { get; init; } = DefaultBarChatHours;

    /// <summary>Whether chats had in the full window are deleted after <see cref="WindowChatDays"/>.</summary>
    public bool DeleteWindowChats { get; init; }

    /// <summary>How many days after it was last added to a chat from the full window is deleted.</summary>
    public int WindowChatDays { get; init; } = DefaultWindowChatDays;

    /// <summary>How long a log (what the Assistant did, in Settings > Activity) is kept when nothing else was chosen, in hours.</summary>
    public const int DefaultLogHours = 48;

    /// <summary>
    /// Whether the logs (Settings > Activity) are deleted after <see cref="LogHours"/>. Unlike the chats, which are the user's own words, a log holds only
    /// what the Assistant did, so this one is on until the user turns it off.
    /// </summary>
    public bool DeleteLogs { get; init; } = true;

    /// <summary>How many hours after it happened a log is deleted.</summary>
    public int LogHours { get; init; } = DefaultLogHours;

    /// <summary>Whether any chat is deleted by time: the bar's or the full window's.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DeletesAnything => DeleteBarChats || DeleteWindowChats;
}