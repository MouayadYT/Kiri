using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Windows.Selection;

namespace Assistant.Windows.Clipboard;

/// <summary>What the clipboard watcher asks of the clipboard, apart so its rules can be tested without the real one.</summary>
internal interface IClipboardAccess
{
    /// <summary>Whether the clipboard holds a format registered under <paramref name="name"/>.</summary>
    bool HasRegisteredFormat(string name);

    /// <summary>The number in a registered format that holds a 32-bit flag, or <see langword="null"/> when it is not there or cannot be read.</summary>
    int? ReadFlag(string name);

    /// <summary>Reads the clipboard's text, no more than <paramref name="maxLength"/> characters of it.</summary>
    ClipboardTextOutcome ReadText(int maxLength, out string? text);
}

/// <summary>
/// The rules that decide whether what was just copied is kept in the clipboard history (PROJECT_SPEC §4.1): only text, and not text an
/// application has marked as not to be kept. Password managers and the like say so with registered clipboard formats that Windows' own
/// clipboard history and cloud sync honor, and so does this: <c>ExcludeClipboardContentFromMonitorProcessing</c> (present),
/// <c>CanIncludeInClipboardHistory</c> (zero), <c>CanUploadToCloudClipboard</c> (zero) and the older <c>Clipboard Viewer Ignore</c>
/// (present). Text longer than the limit is not kept, and text of only white space is not either. Nothing is logged.
/// </summary>
internal static class ClipboardTextFilter
{
    internal const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    internal const string CanUploadToCloud = "CanUploadToCloudClipboard";
    internal const string ViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>The text on the clipboard when it is fit to keep, or <see langword="null"/>.</summary>
    public static string? TryRead(IClipboardAccess clipboard, ClipboardHistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(limits);

        if (clipboard.HasRegisteredFormat(ExcludeFromMonitoring) || clipboard.HasRegisteredFormat(ViewerIgnore))
        {
            return null;
        }

        if (clipboard.ReadFlag(CanIncludeInHistory) == 0 || clipboard.ReadFlag(CanUploadToCloud) == 0)
        {
            return null;
        }

        // One character more than the limit is read, to tell text that is exactly that long from text that is longer.
        if (clipboard.ReadText(limits.MaxItemCharacters + 1, out var text) != ClipboardTextOutcome.Text
            || string.IsNullOrWhiteSpace(text) || text.Length > limits.MaxItemCharacters)
        {
            return null;
        }

        return text;
    }
}
