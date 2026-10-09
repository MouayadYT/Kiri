namespace Assistant.Windows.Selection;

/// <summary>Why the clipboard could not be saved.</summary>
internal enum SnapshotFailure
{
    /// <summary>It was saved.</summary>
    None = 0,

    /// <summary>The clipboard could not be opened.</summary>
    Busy = 1,

    /// <summary>It holds a format that cannot be copied out and put back exactly (an object rendered on demand, a handle of a kind that cannot be duplicated).</summary>
    Unsupported = 2,

    /// <summary>It holds more than is worth keeping in memory for the moment of a copy.</summary>
    TooLarge = 3,
}

/// <summary>One format of the clipboard, as its bytes.</summary>
/// <param name="Format">The clipboard format's number.</param>
/// <param name="Data">What was on the clipboard in that format.</param>
internal sealed record ClipboardFormatData(uint Format, byte[] Data);

/// <summary>Everything that was on the clipboard, in the order its owner put it there. Empty for an empty clipboard.</summary>
internal sealed record ClipboardSnapshot(IReadOnlyList<ClipboardFormatData> Formats)
{
    // Keeps the clipboard's contents out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Formats = {Formats.Count}");
        return true;
    }
}

/// <summary>What reading text off the clipboard came to.</summary>
internal enum ClipboardTextOutcome
{
    /// <summary>There is text, and it was read.</summary>
    Text = 0,

    /// <summary>The clipboard holds no text.</summary>
    NoText = 1,

    /// <summary>The clipboard could not be opened.</summary>
    Busy = 2,
}

/// <summary>
/// The Windows calls the copy fallback makes, apart so the service can be tested without another application or the real clipboard. All of
/// them are called on the one thread the service runs on.
/// </summary>
internal interface IClipboardNativeMethods
{
    /// <summary>The application whose window is in the foreground, or <see langword="null"/> when none is.</summary>
    ForegroundApp? GetForegroundApp();

    /// <summary>The window class of <paramref name="window"/>, or an empty string.</summary>
    string WindowClassOf(nint window);

    /// <summary>Whether the control with the keyboard focus is a password box. Does not throw.</summary>
    bool IsFocusedControlProtected();

    /// <summary>Whether any key of the keyboard is down.</summary>
    bool AreKeysHeld();

    /// <summary>The clipboard's change counter.</summary>
    uint SequenceNumber();

    /// <summary>Copies everything on the clipboard out, or says why it cannot be done exactly. Does not change the clipboard.</summary>
    SnapshotFailure TrySnapshot(out ClipboardSnapshot? snapshot);

    /// <summary>Presses Ctrl+C in the window that has the keyboard. <see langword="false"/> when Windows did not take all of it.</summary>
    bool SendCopy();

    /// <summary>Reads at most <paramref name="maxLength"/> characters of text off the clipboard.</summary>
    ClipboardTextOutcome ReadText(int maxLength, out string? text);

    /// <summary>Replaces the clipboard's contents with <paramref name="snapshot"/>, without telling clipboard history or other devices about it. <see langword="false"/> on failure.</summary>
    bool Restore(ClipboardSnapshot snapshot);
}
