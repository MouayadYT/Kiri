using System.Windows;
using System.Windows.Input;
using Assistant.UI.Settings;

namespace Assistant.UI.Controls;

/// <summary>
/// Lets an element record a shortcut for a <see cref="HotkeyEditor"/>: while the editor is recording, the keys pressed
/// in the element are offered to it as the new shortcut instead of doing what they usually do. A modifier alone waits for
/// the key that goes with it, Esc gives up, and so does moving the keyboard elsewhere.
/// </summary>
public static class HotkeyCapture
{
    /// <summary>Identifies the Editor attached property.</summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.RegisterAttached(
        "Editor", typeof(HotkeyEditor), typeof(HotkeyCapture), new PropertyMetadata(null, OnEditorChanged));

    /// <summary>Gets the editor the element records for.</summary>
    public static HotkeyEditor? GetEditor(DependencyObject element) => (HotkeyEditor?)element.GetValue(EditorProperty);

    /// <summary>Sets the editor the element records for.</summary>
    public static void SetEditor(DependencyObject element, HotkeyEditor? value) => element.SetValue(EditorProperty, value);

    /// <summary>The modifier keys held down now. A test sets it, since it cannot press a key.</summary>
    internal static Func<ModifierKeys> HeldModifiers { get; set; } = () => Keyboard.Modifiers;

    private static void OnEditorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        element.PreviewKeyDown -= OnPreviewKeyDown;
        element.LostKeyboardFocus -= OnLostKeyboardFocus;
        if (e.NewValue is not null)
        {
            element.PreviewKeyDown += OnPreviewKeyDown;
            element.LostKeyboardFocus += OnLostKeyboardFocus;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (GetEditor((DependencyObject)sender) is not { IsRecording: true } editor)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        if (key == Key.Escape)
        {
            editor.CancelRecording();
        }
        else if (HotkeyKeyMap.NameOf(key) is { } name)
        {
            editor.TryAccept(HotkeyKeyMap.ModifiersOf(HeldModifiers()), name);
        }
        else if (key is not (Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin))
        {
            // Some other key, such as a media key: it cannot be a shortcut, and saying so is kinder than ignoring it.
            editor.TryAccept(HotkeyKeyMap.ModifiersOf(HeldModifiers()), string.Empty);
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (GetEditor((DependencyObject)sender) is { IsRecording: true } editor && !((UIElement)sender).IsKeyboardFocusWithin)
        {
            editor.CancelRecording();
        }
    }
}
