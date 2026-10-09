namespace Assistant.Core.QuickSearch.Clipboard;

/// <summary>Text that was just copied. It is private content (PROJECT_SPEC §3.2), so it is never printed or logged.</summary>
public sealed class ClipboardCopiedEventArgs(string text) : EventArgs
{
    /// <summary>What was copied.</summary>
    public string Text { get; } = text;

    /// <inheritdoc/>
    public override string ToString() => $"Length = {Text.Length}";
}

/// <summary>
/// Notices that the user copied text, for the clipboard history (PROJECT_SPEC §4.1, §4.9). It is event-driven, never polling, and
/// runs only while the user has allowed the clipboard history: <see cref="Start"/> when it is turned on, <see cref="Stop"/> when it is
/// turned off or the app closes. It passes on text only, and never what an application says it does not want kept (a password
/// manager's copy, a copy marked as not for history), what is longer than the history keeps, or a copy the Assistant made itself for
/// reading a selection. It never reads Windows' own clipboard history.
/// </summary>
public interface IClipboardWatcher : IDisposable
{
    /// <summary>Raised, from the watcher's own thread, with text that was copied and is fit to keep.</summary>
    event EventHandler<ClipboardCopiedEventArgs>? TextCopied;

    /// <summary>Whether it is watching.</summary>
    bool IsRunning { get; }

    /// <summary>Starts watching, if it is not.</summary>
    void Start();

    /// <summary>Stops watching, if it is, and lets go of everything.</summary>
    void Stop();
}
