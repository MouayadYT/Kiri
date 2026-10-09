using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Assistant.UI.Controls;

/// <summary>
/// Reusable prompt editor. The host owns its glass surface and commands; submission never clears text implicitly.
/// Use PromptInput.Expanded for conversation layouts and AllowMultiline to enable Shift+Enter.
/// </summary>
[TemplatePart(Name = EditorPartName, Type = typeof(TextBox))]
[TemplatePart(Name = MicrophonePartName, Type = typeof(ButtonBase))]
[TemplatePart(Name = CaretPartName, Type = typeof(GlowCaret))]
public class PromptInputControl : Control
{
    private const string EditorPartName = "PART_Editor";
    private const string MicrophonePartName = "Microphone";
    private const string CaretPartName = "PART_Caret";
    private TextBox? _editor;
    private ButtonBase? _microphone;
    private CaretTracker? _caret;
    private bool _isComposing;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PromptInputControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged, (_, value) => value ?? ""));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PromptInputControl), new PropertyMetadata("Ask anything"));

    public static readonly DependencyProperty AllowMultilineProperty = DependencyProperty.Register(
        nameof(AllowMultiline), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
        nameof(SubmitCommand), typeof(ICommand), typeof(PromptInputControl), new PropertyMetadata(null, OnSubmitCommandChanged));

    public static readonly DependencyProperty SubmitCommandParameterProperty = DependencyProperty.Register(
        nameof(SubmitCommandParameter), typeof(object), typeof(PromptInputControl), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty MicrophoneCommandProperty = DependencyProperty.Register(
        nameof(MicrophoneCommand), typeof(ICommand), typeof(PromptInputControl), new PropertyMetadata(null));

    public static readonly DependencyProperty MicrophoneCommandParameterProperty = DependencyProperty.Register(
        nameof(MicrophoneCommandParameter), typeof(object), typeof(PromptInputControl), new PropertyMetadata(null));

    public static readonly DependencyProperty MicrophoneContentProperty = DependencyProperty.Register(
        nameof(MicrophoneContent), typeof(object), typeof(PromptInputControl), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowMicrophoneProperty = DependencyProperty.Register(
        nameof(ShowMicrophone), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(true));

    public static readonly DependencyProperty IsMicrophoneActiveProperty = DependencyProperty.Register(
        nameof(IsMicrophoneActive), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    /// <summary>
    /// The words that are being heard while the user speaks (step 125), drawn in the field in place of the placeholder while the field is empty; empty for
    /// none. Like <see cref="CompletionText"/> it is only drawn, never part of the text.
    /// </summary>
    public static readonly DependencyProperty LiveTextProperty = DependencyProperty.Register(
        nameof(LiveText), typeof(string), typeof(PromptInputControl), new PropertyMetadata("", OnLiveTextChanged, (_, value) => value ?? ""));

    /// <summary>Whether <see cref="LiveText"/> has anything to draw.</summary>
    public static readonly DependencyProperty HasLiveTextProperty = DependencyProperty.Register(
        nameof(HasLiveText), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    /// <summary>
    /// What follows the typed text on a plate, such as the rest of the highlighted result's name and what Enter does with it; empty for
    /// nothing. It is only drawn, never part of the text, and is left out when there is no room for it.
    /// </summary>
    public static readonly DependencyProperty CompletionTextProperty = DependencyProperty.Register(
        nameof(CompletionText), typeof(string), typeof(PromptInputControl), new PropertyMetadata("", OnCompletionChanged, (_, value) => value ?? ""));

    /// <summary>Whether <see cref="CompletionText"/> has anything to draw.</summary>
    public static readonly DependencyProperty HasCompletionProperty = DependencyProperty.Register(
        nameof(HasCompletion), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    /// <summary>What is drawn at the right of the field, before the microphone: the highlighted result's icon.</summary>
    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(object), typeof(PromptInputControl), new PropertyMetadata(null, OnTrailingContentChanged));

    /// <summary>Whether <see cref="TrailingContent"/> has something to draw.</summary>
    public static readonly DependencyProperty HasTrailingContentProperty = DependencyProperty.Register(
        nameof(HasTrailingContent), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    /// <summary>Words drawn at the right of the field when there is no icon there: where what is typed goes ("Ask").</summary>
    public static readonly DependencyProperty TrailingLabelProperty = DependencyProperty.Register(
        nameof(TrailingLabel), typeof(string), typeof(PromptInputControl), new PropertyMetadata("", OnTrailingLabelChanged, (_, value) => value ?? ""));

    /// <summary>Whether <see cref="TrailingLabel"/> has words.</summary>
    public static readonly DependencyProperty HasTrailingLabelProperty = DependencyProperty.Register(
        nameof(HasTrailingLabel), typeof(bool), typeof(PromptInputControl), new PropertyMetadata(false));

    /// <summary>Can also be used by a host's send button with this control as CommandTarget.</summary>
    public static RoutedUICommand Submit { get; } = new("Submit prompt", nameof(Submit), typeof(PromptInputControl));

    static PromptInputControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PromptInputControl),
            new FrameworkPropertyMetadata(typeof(PromptInputControl)));
    }

    public PromptInputControl()
    {
        CommandBindings.Add(new CommandBinding(Submit,
            (_, e) => { TrySubmit(); e.Handled = true; },
            (_, e) => { e.CanExecute = CanSubmit(); e.Handled = true; }));
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The words being heard while the user speaks; empty for none.</summary>
    public string LiveText { get => (string)GetValue(LiveTextProperty); set => SetValue(LiveTextProperty, value); }

    /// <summary>Whether <see cref="LiveText"/> has anything to draw.</summary>
    public bool HasLiveText { get => (bool)GetValue(HasLiveTextProperty); private set => SetValue(HasLiveTextProperty, value); }

    /// <summary>What follows the typed text on a plate; empty for nothing.</summary>
    public string CompletionText { get => (string)GetValue(CompletionTextProperty); set => SetValue(CompletionTextProperty, value); }

    /// <summary>Whether <see cref="CompletionText"/> has anything to draw.</summary>
    public bool HasCompletion { get => (bool)GetValue(HasCompletionProperty); private set => SetValue(HasCompletionProperty, value); }

    /// <summary>What is drawn before the microphone, such as the highlighted result's icon; <see langword="null"/> for nothing.</summary>
    public object? TrailingContent { get => GetValue(TrailingContentProperty); set => SetValue(TrailingContentProperty, value); }

    /// <summary>Whether <see cref="TrailingContent"/> has something to draw.</summary>
    public bool HasTrailingContent { get => (bool)GetValue(HasTrailingContentProperty); private set => SetValue(HasTrailingContentProperty, value); }

    /// <summary>Words drawn before the microphone when there is no <see cref="TrailingContent"/>.</summary>
    public string TrailingLabel { get => (string)GetValue(TrailingLabelProperty); set => SetValue(TrailingLabelProperty, value); }

    /// <summary>Whether <see cref="TrailingLabel"/> has words.</summary>
    public bool HasTrailingLabel { get => (bool)GetValue(HasTrailingLabelProperty); private set => SetValue(HasTrailingLabelProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public bool AllowMultiline { get => (bool)GetValue(AllowMultilineProperty); set => SetValue(AllowMultilineProperty, value); }
    public ICommand? SubmitCommand { get => (ICommand?)GetValue(SubmitCommandProperty); set => SetValue(SubmitCommandProperty, value); }

    /// <summary>Passed to SubmitCommand; null uses the current, untrimmed Text.</summary>
    public object? SubmitCommandParameter { get => GetValue(SubmitCommandParameterProperty); set => SetValue(SubmitCommandParameterProperty, value); }
    public ICommand? MicrophoneCommand { get => (ICommand?)GetValue(MicrophoneCommandProperty); set => SetValue(MicrophoneCommandProperty, value); }
    public object? MicrophoneCommandParameter { get => GetValue(MicrophoneCommandParameterProperty); set => SetValue(MicrophoneCommandParameterProperty, value); }

    /// <summary>Optional replacement for the default microphone glyph.</summary>
    public object? MicrophoneContent { get => GetValue(MicrophoneContentProperty); set => SetValue(MicrophoneContentProperty, value); }
    public bool ShowMicrophone { get => (bool)GetValue(ShowMicrophoneProperty); set => SetValue(ShowMicrophoneProperty, value); }

    /// <summary>Whether the host's voice input is listening; the microphone glyph brightens while it is.</summary>
    public bool IsMicrophoneActive { get => (bool)GetValue(IsMicrophoneActiveProperty); set => SetValue(IsMicrophoneActiveProperty, value); }

    public override void OnApplyTemplate()
    {
        if (_editor is not null)
        {
            DataObject.RemovePastingHandler(_editor, OnHandyPaste);
            _editor.PreviewKeyDown -= OnEditorKeyDown;
            TextCompositionManager.RemovePreviewTextInputStartHandler(_editor, OnCompositionStarted);
            TextCompositionManager.RemovePreviewTextInputHandler(_editor, OnCompositionCompleted);
            _editor.LostKeyboardFocus -= OnEditorLostFocus;
        }

        if (_microphone is not null)
        {
            _microphone.Click -= OnMicrophoneClick;
        }

        _caret?.Dispose();
        _caret = null;
        base.OnApplyTemplate();
        _isComposing = false;
        _microphone = GetTemplateChild(MicrophonePartName) as ButtonBase;
        if (_microphone is not null)
        {
            _microphone.Click += OnMicrophoneClick;
        }

        _editor = GetTemplateChild(EditorPartName) as TextBox;
        if (_editor is not null)
        {
            DataObject.AddPastingHandler(_editor, OnHandyPaste);

            // A picture on the clipboard is pasted too, where the host takes pictures: the editor itself only knows text.
            CommandManager.AddPreviewCanExecuteHandler(_editor, OnPreviewCanPaste);
            CommandManager.AddPreviewExecutedHandler(_editor, OnPreviewPaste);
            _editor.PreviewKeyDown += OnEditorKeyDown;
            TextCompositionManager.AddPreviewTextInputStartHandler(_editor, OnCompositionStarted);
            TextCompositionManager.AddPreviewTextInputHandler(_editor, OnCompositionCompleted);
            _editor.LostKeyboardFocus += OnEditorLostFocus;
            if (GetTemplateChild(CaretPartName) is GlowCaret caret)
            {
                _caret = new CaretTracker(_editor, caret);
            }
        }
    }

    private void OnHandyPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (IsMicrophoneActive && _editor?.IsKeyboardFocusWithin == true && e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            && e.DataObject.GetData(DataFormats.UnicodeText) is string text && Assistant.UI.Voice.HandySpeechToTextService.Active?.AcceptPaste(text) == true)
            e.CancelCommand();
    }

    /// <summary>
    /// Raised when the user pastes while the clipboard holds a picture, or image files, and no text: the host attaches them to what is being asked.
    /// A host that does not listen gets nothing pasted, as before.
    /// </summary>
    public event EventHandler<IReadOnlyList<Assistant.UI.Messages.ImageItem>>? PicturesPasted;

    private void OnPreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && PicturesPasted is not null && Assistant.UI.Messages.ClipboardPictures.HasPictures())
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private void OnPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && PicturesPasted is { } pasted && Assistant.UI.Messages.ClipboardPictures.HasPictures()
            && Assistant.UI.Messages.ClipboardPictures.Read() is { Count: > 0 } pictures)
        {
            e.Handled = true;
            pasted(this, pictures);
        }
    }

    /// <summary>The glowing caret's tracker, while the template has one.</summary>
    internal CaretTracker? Caret => _caret;

    /// <summary>Moves keyboard focus to the editor without replacing a binding or selecting existing text.</summary>
    public bool FocusInput()
    {
        ApplyTemplate();
        return _editor?.Focus() == true;
    }

    /// <summary>
    /// Moves keyboard focus to the editor with all its text selected, as the bar does when it opens again on what was typed before: typing replaces
    /// it, and the right arrow carries on after it.
    /// </summary>
    public bool FocusInputSelectingAll()
    {
        // The text is selected whether or not Windows gives the keyboard at once: the selection is there when it does.
        var focused = FocusInput();
        _editor?.SelectAll();
        return focused;
    }

    /// <summary>What is selected in the editor; empty for nothing.</summary>
    internal string SelectedText => _editor?.SelectedText ?? "";

    /// <summary>Moves keyboard focus to the editor with the caret after its text, as when the host has just written the text for the user.</summary>
    public bool FocusInputAtEnd()
    {
        if (!FocusInput() || _editor is null)
        {
            return false;
        }

        _editor.CaretIndex = _editor.Text.Length;
        return true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!e.Handled)
        {
            e.Handled = FocusInput();
        }
    }

    /// <summary>Executes an enabled command for nonblank text, preserving the draft if the host does not clear it.</summary>
    public bool TrySubmit()
    {
        _editor?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        GetBindingExpression(TextProperty)?.UpdateSource();
        if (!CanSubmit())
        {
            return false;
        }

        var parameter = SubmitCommandParameter ?? Text;
        if (SubmitCommand is RoutedCommand routed)
        {
            routed.Execute(parameter, _editor ?? (IInputElement)this);
        }
        else
        {
            SubmitCommand!.Execute(parameter);
        }

        return true;
    }

    private bool CanSubmit()
    {
        if (!IsEnabled || _isComposing || string.IsNullOrWhiteSpace(Text))
        {
            return false;
        }

        var parameter = SubmitCommandParameter ?? Text;
        return SubmitCommand is RoutedCommand routed
            ? routed.CanExecute(parameter, _editor ?? (IInputElement)this)
            : SubmitCommand?.CanExecute(parameter) == true;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        // IME owns Enter while composing. Other modified Enters must not accidentally send a prompt.
        if (e.Key != Key.Enter || _isComposing)
        {
            return;
        }

        e.Handled = HandleEnter(Keyboard.Modifiers, e.IsRepeat);
    }

    // False leaves Shift+Enter to the native TextBox editing command (including selection and undo). Ctrl+Enter always submits: in the
    // Search or Ask bar it is the way to ask whatever the results say (PROJECT_SPEC §4.1).
    internal bool HandleEnter(ModifierKeys modifiers, bool isRepeat)
    {
        if (modifiers == ModifierKeys.Shift && AllowMultiline)
        {
            return false;
        }

        if (!isRepeat && (modifiers is ModifierKeys.None or ModifierKeys.Shift or ModifierKeys.Control))
        {
            TrySubmit();
        }

        return true;
    }

    // A pointer click on the microphone leaves typing where it was; keyboard users who moved to it stay on it.
    private void OnMicrophoneClick(object sender, RoutedEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is MouseDevice or StylusDevice)
        {
            FocusInput();
        }
    }

    private void OnCompositionStarted(object sender, TextCompositionEventArgs e) => _isComposing = true;
    private void OnCompositionCompleted(object sender, TextCompositionEventArgs e) => _isComposing = false;
    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _isComposing = false;
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => CommandManager.InvalidateRequerySuggested();

    private static void OnLiveTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PromptInputControl)d).HasLiveText = !string.IsNullOrEmpty((string)e.NewValue);

    private static void OnCompletionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PromptInputControl)d).HasCompletion = !string.IsNullOrEmpty((string)e.NewValue);

    private static void OnTrailingContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PromptInputControl)d).HasTrailingContent = e.NewValue is not null;

    private static void OnTrailingLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PromptInputControl)d).HasTrailingLabel = !string.IsNullOrEmpty((string)e.NewValue);

    private static void OnSubmitCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PromptInputControl)d;
        if (e.OldValue is ICommand oldCommand)
        {
            CanExecuteChangedEventManager.RemoveHandler(oldCommand, control.OnCanExecuteChanged);
        }

        if (e.NewValue is ICommand newCommand)
        {
            CanExecuteChangedEventManager.AddHandler(newCommand, control.OnCanExecuteChanged);
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => CommandManager.InvalidateRequerySuggested();
}
