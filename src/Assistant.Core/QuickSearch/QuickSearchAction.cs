using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>What a <see cref="QuickSearchAction"/> does. Every one is deterministic and runs outside the model.</summary>
public enum QuickSearchActionKind
{
    /// <summary>Opens an installed application. The target is its launch identity.</summary>
    LaunchApplication = 0,

    /// <summary>Opens a file or folder with its default handler. The target is its full path.</summary>
    OpenPath = 1,

    /// <summary>Shows a file or folder, selected, in File Explorer. The target is its full path.</summary>
    RevealPath = 2,

    /// <summary>Copies a file's or folder's full path to the clipboard. The target is the path.</summary>
    CopyPath = 3,

    /// <summary>Attaches a picture or a document to a new conversation. The target is its full path.</summary>
    AttachFile = 4,

    /// <summary>Runs one of the Assistant's own actions. The target is the action's id, the argument what it was given.</summary>
    RunAction = 5,

    /// <summary>Puts text back on the clipboard. The target is the id of the clipboard history item.</summary>
    CopyClipboardItem = 6,

    /// <summary>Attaches text from the clipboard history to a new conversation. The target is the item's id.</summary>
    AttachClipboardItem = 7,

    /// <summary>Takes an item out of the clipboard history. The target is the item's id.</summary>
    RemoveClipboardItem = 8,

    /// <summary>Copies a file or a folder itself to the clipboard, to be pasted somewhere as File Explorer's Copy would. The target is its full path.</summary>
    CopyFile = 9,
}

/// <summary>
/// One thing a quick-search result can do, as data: what kind of thing, in words for the user, and what it is done to. The app runs
/// it (PROJECT_SPEC §4.1), so a result never carries code, and nothing the model says can add or change one. The target and the
/// argument can be private content (a path, copied text), so they are never printed or logged.
/// </summary>
/// <param name="Kind">What it does.</param>
/// <param name="Title">What it does, in words, as it is listed: "Open", "Show in File Explorer".</param>
/// <param name="Target">What it is done to; see <see cref="QuickSearchActionKind"/>.</param>
/// <param name="Argument">What it was given, such as a volume level, or <see langword="null"/>.</param>
public sealed record QuickSearchAction(QuickSearchActionKind Kind, string Title, string Target, string? Argument = null)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}
