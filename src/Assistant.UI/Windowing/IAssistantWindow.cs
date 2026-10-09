using Assistant.Windows.Placement;

namespace Assistant.UI.Windowing;

/// <summary>
/// The Assistant's one window as its <see cref="AssistantWindowStateController"/> sees it: a surface that is either the
/// compact bar or the floating conversation, and grows from one into the other without ever being two windows.
/// </summary>
internal interface IAssistantWindow
{
    /// <summary>
    /// What the window shows now. It changes to <see cref="AssistantWindowState.FloatingConversation"/> as soon as the
    /// bar starts growing, and back to <see cref="AssistantWindowState.Compact"/> once the window is hidden.
    /// </summary>
    AssistantWindowState State { get; }

    /// <summary>Where the top center of the glass is on screen, in physical pixels, or <see langword="null"/> if unknown.</summary>
    ScreenPoint? SurfaceTop { get; }

    /// <summary>
    /// Whether the window is on screen and not on its way out, which is what the shortcut asks to know whether to open it or put it away.
    /// A window that does not say is taken to be away.
    /// </summary>
    bool IsShowing => false;

    /// <summary>Raised after the user has dragged the window somewhere else.</summary>
    event EventHandler? Moved;

    /// <summary>Raised when the bar has finished growing into the conversation.</summary>
    event EventHandler? Expanded;

    /// <summary>Raised when the window goes from shown to hidden, however it was hidden.</summary>
    event EventHandler? Hidden;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler? AssistantStateChanged;

    /// <summary>
    /// Raised when the user pastes pictures into the bar: they start a conversation that has them attached, as a picture attached from the bar's results
    /// does. A window that does not say never raises it.
    /// </summary>
    event EventHandler<IReadOnlyList<Assistant.UI.Messages.ImageItem>>? PicturesPasted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Brings the window forward and focuses it. A hidden window opens as the compact bar, at
    /// <paramref name="surfaceTop"/> if given and otherwise at its default place; one that is already showing stays
    /// where it is, in whichever state it is in.
    /// </summary>
    void ShowAndFocus(ScreenPoint? surfaceTop);

    /// <summary>Invokes the assistant on the monitor containing the mouse pointer.</summary>
    void ShowAtPointer() => ShowAndFocus(null);

    /// <summary>
    /// Shows the window as the floating conversation, for a way in that does not start from the bar: a hidden window opens as
    /// the conversation, at <paramref name="surfaceTop"/> if given and otherwise at its default place, a visible bar grows into
    /// it where it is, and a conversation that shows is brought forward.
    /// </summary>
    void ShowConversation(ScreenPoint? surfaceTop);

    /// <summary>
    /// Shows the window as the floating conversation beside another application's window, for a way in that starts in it (the text
    /// selected in a browser): the panel opens inside <paramref name="target"/>, against its right edge, out of the way of the pointer
    /// (<see cref="NearWindowTarget"/>). A hidden window opens there, and a conversation that already shows moves there. A bar that
    /// shows grows into the conversation where it is. When <paramref name="target"/> is on no monitor, it is
    /// <see cref="ShowConversation"/>.
    /// </summary>
    void ShowConversationNear(NearWindowTarget target);

    /// <summary>
    /// Makes the shown window the foreground one and puts the keyboard in it, for a way in that had to read another application first
    /// (the selected-text shortcut), where Windows may have taken back the right to bring a window forward by the time the answer is in.
    /// </summary>
    /// <returns>Whether the window is in front and has the keyboard.</returns>
    bool TakeForeground();

    /// <summary>Grows the compact bar into the floating conversation, where it is, and focuses the conversation.</summary>
    void ExpandToConversation();

    /// <summary>
    /// The bar's question has been asked: while its answer is on its way the bar folds into the Working pill, which says what the Assistant is
    /// doing ("Working", "Looking into it"), and when the answer comes the pill opens into the floating conversation with a spring. An answer that is
    /// already there makes the bar grow into the conversation at once, as <see cref="ExpandToConversation"/> does. An answer that ends with nothing
    /// to show (it was stopped) puts the pill away.
    /// </summary>
    void AwaitAnswer() => ExpandToConversation();

    /// <summary>Turns voice input off, animates the window out and hides it.</summary>
    void Dismiss();

    /// <summary>
    /// Turns voice input off and hides the window at once, with no animation and nothing left on screen, for what must see the screen
    /// without it: the capture for Visual Intelligence. Its draft and its conversation are kept, as with <see cref="Dismiss"/>.
    /// </summary>
    /// <returns>Whether it was showing, so that Windows has a moment to take it off the screen.</returns>
    bool HideNow();
}
