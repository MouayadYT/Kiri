namespace Assistant.Windows.Selection;

/// <summary>What asking the focused control for its selection came to, before it is turned into a <see cref="SelectionResult"/>.</summary>
internal enum SelectionProbeOutcome
{
    /// <summary>Text is selected, and <see cref="SelectionProbe.Text"/> has it.</summary>
    Selected = 0,

    /// <summary>The control exposes its selection and nothing is selected.</summary>
    Empty = 1,

    /// <summary>Neither the focused control nor any control around it exposes a text pattern.</summary>
    NoTextPattern = 2,

    /// <summary>The control has a text pattern but says it supports no selection.</summary>
    NoSelectionSupport = 3,

    /// <summary>Nothing has the keyboard focus, as far as UI Automation can tell.</summary>
    NoFocusedControl = 4,

    /// <summary>The focused control is a password box, whose text is never read.</summary>
    Protected = 5,

    /// <summary>UI Automation failed or the control went away while it was asked.</summary>
    Failed = 6,

    /// <summary>The application did not answer within the time allowed.</summary>
    TimedOut = 7,
}

/// <summary>The answer of the focused control.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Text">The selected text, at most one character more than was asked for; only with <see cref="SelectionProbeOutcome.Selected"/>.</param>
/// <param name="ControlProcessId">The process the focused control belongs to, or 0 when not known.</param>
/// <param name="FailureType">The type name of what failed, with <see cref="SelectionProbeOutcome.Failed"/>; never a message, which could quote the text.</param>
internal sealed record SelectionProbe(
    SelectionProbeOutcome Outcome, string? Text = null, int ControlProcessId = 0, string? FailureType = null);

/// <summary>The Windows calls the selection service makes, apart so the service can be tested without another application.</summary>
internal interface ISelectionNativeMethods
{
    /// <summary>The application whose window is in the foreground, or <see langword="null"/> when none is.</summary>
    ForegroundApp? GetForegroundApp();

    /// <summary>The process <paramref name="processId"/>, shown by <paramref name="window"/>, or <see langword="null"/> when it is gone.</summary>
    ForegroundApp? DescribeProcess(int processId, nint window);

    /// <summary>
    /// Asks the control with the keyboard focus for its selected text, reading at most <paramref name="maxLength"/> + 1 characters.
    /// It blocks for as long as the application takes, so it is called on a thread of its own. It does not throw.
    /// </summary>
    SelectionProbe ReadSelection(int maxLength);
}
