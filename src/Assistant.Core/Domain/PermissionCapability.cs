namespace Assistant.Core.Domain;

/// <summary>
/// A kind of access the user can allow or refuse the Assistant (PROJECT_SPEC §3.1 P2 and P8, §4.9). Allowing one never
/// makes the Assistant look on its own: it still reads, captures or changes something only in direct response to what the
/// user asks. The permission decides whether it may when they do.
/// </summary>
public enum PermissionCapability
{
    /// <summary>Reading files: the ones the user attaches to a question, and the ones file search finds for it.</summary>
    Files = 0,

    /// <summary>Capturing a part of the screen for Visual Intelligence.</summary>
    ScreenCapture = 1,

    /// <summary>Reading the text selected in another app, from the shortcut for selected-text actions.</summary>
    SelectedText = 2,

    /// <summary>Reading the user's calendar.</summary>
    Calendar = 3,

    /// <summary>Reading the user's messages and contacts in messaging apps.</summary>
    Messaging = 4,

    /// <summary>Searching the web or finding images online, which sends a query off this PC.</summary>
    ExternalSearch = 5,

    /// <summary>Deleting, overwriting or otherwise destroying data.</summary>
    DestructiveActions = 6,

    /// <summary>
    /// Reading the selection of an app that does not share it by pressing Copy (Ctrl+C) in that app and reading the clipboard, from its own
    /// shortcut. Unlike <see cref="SelectedText"/> this acts on the other app and on the clipboard, so it starts off.
    /// </summary>
    SelectedTextByCopy = 7,

    /// <summary>
    /// Keeping a short history of the text the user copies, in memory only, so the bar can search it (PROJECT_SPEC §4.1, §4.9). It is the
    /// one capability that watches something while nobody asks, so it starts off and the user turns it on themselves.
    /// </summary>
    ClipboardHistory = 8,
}
