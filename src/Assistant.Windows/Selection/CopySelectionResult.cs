using System.Text;

namespace Assistant.Windows.Selection;

/// <summary>What asking an application to copy its selection came to (PROJECT_SPEC §4.5, capture order 2).</summary>
public enum CopySelectionStatus
{
    /// <summary>The application copied text, and <see cref="CopySelectionResult.Text"/> has it.</summary>
    Copied = 0,

    /// <summary>
    /// The application did not put anything on the clipboard in time, or only blanks: nothing is selected, or the application did not take the
    /// key press (an application that runs with more rights than this one ignores it). The clipboard was not changed, or was put back.
    /// </summary>
    NothingCopied = 1,

    /// <summary>The application copied something that is not text (a picture, a file). The previous clipboard was put back when it could be.</summary>
    NotText = 2,

    /// <summary>The application in front is one where Copy is not safe to press, such as a terminal, where Ctrl+C stops the running program. Nothing was sent.</summary>
    UnsafeApp = 3,

    /// <summary>The control with the keyboard is a password box. Nothing was sent.</summary>
    ProtectedControl = 4,

    /// <summary>
    /// The clipboard holds something that cannot be saved whole and put back (an object another application renders on demand, a very large
    /// one), so Copy was not pressed: it would have lost it. Nothing was sent.
    /// </summary>
    ClipboardNotSaved = 5,

    /// <summary>The clipboard could not be opened (another application kept it). Nothing was sent, or what came back could not be read.</summary>
    ClipboardBusy = 6,

    /// <summary>The user was still holding keys of the shortcut, which would have turned Copy into another shortcut. Nothing was sent.</summary>
    KeysHeld = 7,

    /// <summary>The window in front changed before Copy was pressed. Nothing was sent.</summary>
    ForegroundChanged = 8,

    /// <summary>No window is in the foreground (a locked PC, a secure desktop), so there is nobody to copy from.</summary>
    NoForegroundApp = 9,

    /// <summary>Something went wrong that has no words of its own (the key press could not be sent, the work did not finish in time).</summary>
    Failed = 10,

    /// <summary>The Assistant's own window is the one in front, which has no other application's selection to copy. Nothing was sent.</summary>
    OwnWindow = 11,

    /// <summary>A permission it needs (Selected Text, Selected Text by Copy) does not allow it (step 119): it is off, or set to ask every time and this use was not asked about. Nothing was sent.</summary>
    NotAllowed = 12,
}

/// <summary>What became of the user's own clipboard around a copy.</summary>
public enum ClipboardRestoreOutcome
{
    /// <summary>The clipboard was never changed, so there was nothing to put back.</summary>
    NotNeeded = 0,

    /// <summary>The previous contents are back on the clipboard.</summary>
    Restored = 1,

    /// <summary>Something else (another application, or the user) changed the clipboard after the copy, so the previous contents were not put over it.</summary>
    ChangedByOther = 2,

    /// <summary>Putting the previous contents back failed, and the clipboard holds what was copied.</summary>
    Failed = 3,
}

/// <summary>
/// The answer to "copy what is selected": a <see cref="Status"/>, the copied text when there is some, the application it came from, and what
/// became of the user's clipboard, which the caller tells the user.
/// </summary>
/// <remarks>The text is the user's private content, so it is left out of <see cref="object.ToString"/> and out of logs.</remarks>
public sealed record CopySelectionResult
{
    private CopySelectionResult(
        CopySelectionStatus status, ForegroundApp? app, string? text, bool isTruncated, ClipboardRestoreOutcome restore)
    {
        Status = status;
        App = app;
        Text = text;
        IsTruncated = isTruncated;
        Restore = restore;
    }

    /// <summary>What the answer is.</summary>
    public CopySelectionStatus Status { get; }

    /// <summary>The application that was asked, or <see langword="null"/> when none was in front.</summary>
    public ForegroundApp? App { get; }

    /// <summary>The copied text, or <see langword="null"/> unless <see cref="Status"/> is <see cref="CopySelectionStatus.Copied"/>.</summary>
    public string? Text { get; }

    /// <summary>Whether more text was copied than <see cref="SelectionService.MaxTextLength"/> allows, so <see cref="Text"/> ends before it does.</summary>
    public bool IsTruncated { get; }

    /// <summary>What became of the clipboard the user had before.</summary>
    public ClipboardRestoreOutcome Restore { get; }

    /// <summary>Whether text was copied and <see cref="Text"/> has it.</summary>
    public bool HasText => Status == CopySelectionStatus.Copied;

    internal static CopySelectionResult Copied(ForegroundApp app, string text, bool isTruncated, ClipboardRestoreOutcome restore) =>
        new(CopySelectionStatus.Copied, app, text, isTruncated, restore);

    internal static CopySelectionResult Of(CopySelectionStatus status, ForegroundApp? app, ClipboardRestoreOutcome restore = ClipboardRestoreOutcome.NotNeeded) =>
        new(status, app, null, false, restore);

    // Keeps the copied text out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Status = {Status}, App = {App?.ProcessName}, Length = {Text?.Length ?? 0}, IsTruncated = {IsTruncated}, Restore = {Restore}");
        return true;
    }
}
