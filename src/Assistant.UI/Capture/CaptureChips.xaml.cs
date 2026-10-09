using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Assistant.UI.Capture;

/// <summary>
/// What an action chip says about whether it can be used: <see cref="IsEnabled"/>, and a sentence to show when the pointer rests on it,
/// which says why not when it cannot be used.
/// </summary>
/// <param name="IsEnabled">Whether the chip can be pressed.</param>
/// <param name="Hint">What the chip's tooltip says.</param>
internal sealed record ChipAvailability(bool IsEnabled, string Hint)
{
    /// <summary>What Image Search says until a way of searching with an image exists.</summary>
    public static ChipAvailability ImageSearchUnavailable { get; } = new(
        false, "Image Search sends the picture to a search provider on the web, which isn't available yet.");
}

/// <summary>
/// The action chips beside the selection of the Visual Intelligence overlay (PROJECT_SPEC §4.6): Ask Assistant, a field with a
/// caret in it where a question can be typed, Image Search, and Copy. The chips are glass over the overlay's surface
/// (<see cref="Controls.FrostedPill"/>); the overlay's window gives them that source, and decides what each chip does. Pressing Enter in the field,
/// or its mark, asks; the others are buttons.
/// </summary>
internal sealed partial class CaptureChips : UserControl
{
    public CaptureChips()
    {
        InitializeComponent();
        ImageSearchButton.Click += (_, _) => ImageSearchRequested?.Invoke(this, EventArgs.Empty);
        CopyButton.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        AskMark.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AskRequested?.Invoke(this, AskBox.Text);
        };
        AskBox.PreviewKeyDown += OnAskKeyDown;
        AskBox.TextChanged += (_, _) => AskPlaceholder.Visibility = AskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetImageSearch(ChipAvailability.ImageSearchUnavailable);
    }

    /// <summary>Raised when the user asks (Enter in the field, or the mark): the question typed, which is empty when none was.</summary>
    public event EventHandler<string>? AskRequested;

    /// <summary>Raised when the user presses Copy.</summary>
    public event EventHandler? CopyRequested;

    /// <summary>Raised when the user presses Image Search, which can only happen while it is enabled.</summary>
    public event EventHandler? ImageSearchRequested;

    /// <summary>What has been typed in the Ask chip.</summary>
    public string Question
    {
        get => AskBox.Text;
        set => AskBox.Text = value;
    }

    /// <summary>The Ask chip's field.</summary>
    internal TextBox AskField => AskBox;

    /// <summary>The Image Search chip.</summary>
    internal Button ImageSearchChip => ImageSearchButton;

    /// <summary>The Copy chip.</summary>
    internal Button CopyChip => CopyButton;

    /// <summary>Shows Image Search as usable or not, with the sentence its tooltip says.</summary>
    public void SetImageSearch(ChipAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ImageSearchButton.IsEnabled = availability.IsEnabled;
        ImageSearchButton.ToolTip = availability.Hint;
        ToolTipService.SetShowOnDisabled(ImageSearchButton, true);
        AutomationProperties.SetHelpText(ImageSearchButton, availability.Hint);
    }

    /// <summary>Puts the keyboard in the Ask chip's field, where the user types what they want to know.</summary>
    public void FocusAsk()
    {
        AskBox.Focus();
        Keyboard.Focus(AskBox);
    }

    private void OnAskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            AskRequested?.Invoke(this, AskBox.Text);
        }
    }
}
