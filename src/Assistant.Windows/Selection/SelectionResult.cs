using System.Text;

namespace Assistant.Windows.Selection;

/// <summary>What asking the foreground application for its selected text came to.</summary>
public enum SelectionStatus
{
    /// <summary>Text is selected, and <see cref="SelectionResult.Text"/> has it.</summary>
    Selected = 0,

    /// <summary>The application does expose its selection, and nothing is selected (or only blanks, or only the caret).</summary>
    NoSelection = 1,

    /// <summary>
    /// The application does not expose its selection to UI Automation (or the focused control is a password box, or the application did
    /// not answer in time). Unlike <see cref="NoSelection"/>, that says nothing about whether text is selected, so another way of
    /// capturing it may be worth trying.
    /// </summary>
    Unsupported = 2,

    /// <summary>No window is in the foreground, as while the PC is locked or a secure desktop shows, so there is nobody to ask.</summary>
    NoForegroundApp = 3,

    /// <summary>The Selected Text permission does not allow it (step 119): it is off, or set to ask every time and this use was not asked about. The application was not asked.</summary>
    NotAllowed = 4,
}

/// <summary>
/// The answer to "what is selected?": a <see cref="Status"/> that says so plainly, the selected text when there is some, and the
/// application it came from. Nothing is thrown for an application that will not say.
/// </summary>
/// <remarks>The text is the user's private content, so it is left out of <see cref="object.ToString"/> and out of logs.</remarks>
public sealed record SelectionResult
{
    private SelectionResult(SelectionStatus status, ForegroundApp? app, string? text, bool isTruncated)
    {
        Status = status;
        App = app;
        Text = text;
        IsTruncated = isTruncated;
    }

    /// <summary>What the answer is.</summary>
    public SelectionStatus Status { get; }

    /// <summary>The application the selection is in, or <see langword="null"/> when <see cref="Status"/> is <see cref="SelectionStatus.NoForegroundApp"/>.</summary>
    public ForegroundApp? App { get; }

    /// <summary>The selected text exactly as the application gave it, or <see langword="null"/> unless <see cref="Status"/> is <see cref="SelectionStatus.Selected"/>.</summary>
    public string? Text { get; }

    /// <summary>Whether more text is selected than <see cref="SelectionService.MaxTextLength"/> allows, so <see cref="Text"/> ends before the selection does.</summary>
    public bool IsTruncated { get; }

    /// <summary>Whether text was selected and <see cref="Text"/> has it.</summary>
    public bool HasText => Status == SelectionStatus.Selected;

    internal static SelectionResult Selected(ForegroundApp app, string text, bool isTruncated) =>
        new(SelectionStatus.Selected, app, text, isTruncated);

    internal static SelectionResult NoSelection(ForegroundApp app) => new(SelectionStatus.NoSelection, app, null, false);

    internal static SelectionResult Unsupported(ForegroundApp app) => new(SelectionStatus.Unsupported, app, null, false);

    internal static SelectionResult NoForegroundApp() => new(SelectionStatus.NoForegroundApp, null, null, false);

    internal static SelectionResult NotAllowed() => new(SelectionStatus.NotAllowed, null, null, false);

    // Keeps the selected text out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Status = {Status}, App = {App?.ProcessName}, Length = {Text?.Length ?? 0}, IsTruncated = {IsTruncated}");
        return true;
    }
}
