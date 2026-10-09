using System.Runtime.InteropServices;
using System.Windows;
using Assistant.UI.Messages;

namespace Assistant.UI.Windowing;

/// <summary>
/// The Windows clipboard. Text goes on it the plain Win32 way (<see cref="Assistant.Windows.Clipboard.TextClipboardWriter"/>), which does not make the
/// window wait the way WPF's own does; files go through WPF, which retries while another app has the clipboard open.
/// </summary>
internal sealed class WpfTextClipboard : ITextClipboard
{
    /// <inheritdoc/>
    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // False when another app kept the clipboard open. What was being copied is never logged (PROJECT_SPEC §3.2).
        return Assistant.Windows.Clipboard.TextClipboardWriter.TrySetText(text);
    }

    /// <inheritdoc/>
    public bool TrySetFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return false;
        }

        try
        {
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange([.. paths]);
            Clipboard.SetFileDropList(files);
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException)
        {
            return false;
        }
    }
}
