namespace Assistant.Core.QuickSearch;

/// <summary>The words the same action is listed under wherever it is offered, so a person reads one name for one thing.</summary>
public static class QuickSearchActionTitles
{
    /// <summary>Opens an application, a file or a folder.</summary>
    public const string Open = "Open";

    /// <summary>Runs one of the Assistant's own actions.</summary>
    public const string Run = "Run";

    /// <summary>Shows a file or folder in File Explorer, selected in the folder it is in.</summary>
    public const string Reveal = "Open file location";

    /// <summary>Copies a file itself to the clipboard, to be pasted somewhere.</summary>
    public const string CopyFile = "Copy file";

    /// <summary>Copies a folder itself to the clipboard, to be pasted somewhere.</summary>
    public const string CopyFolder = "Copy folder";

    /// <summary>Copies a path to the clipboard.</summary>
    public const string CopyPath = "Copy path";

    /// <summary>Attaches a picture, a document or some text to a new conversation.</summary>
    public const string Attach = "Attach to conversation";

    /// <summary>Puts copied text back on the clipboard.</summary>
    public const string Copy = "Copy";

    /// <summary>Takes an item out of the clipboard history.</summary>
    public const string RemoveFromHistory = "Remove from history";
}
