using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace Assistant.UI.Controls;

/// <summary>
/// A <see cref="TextBlock"/> whose words can be selected with the mouse and copied (Ctrl+C, or Copy under the right mouse button), where the view it is in
/// says so (<see cref="IsSelectionEnabledProperty"/>, which is inherited: the History window sets it on its conversation, and every message drawn there
/// follows). Anywhere else it is a plain text block. It is drawn and measured exactly as a text block is, since it is one: what is added is the editor WPF
/// itself gives a read-only <see cref="RichTextBox"/>, attached to the block's own text. WPF does not offer that for a text block in public, so it is reached
/// by reflection; where it cannot be reached the text simply cannot be selected, and nothing else changes. A selection stays inside one block (a paragraph, a
/// list item, a bubble); the button under an answer copies all of it.
/// </summary>
public class SelectableTextBlock : TextBlock
{
    /// <summary>Whether the text blocks inside the element it is set on let their words be selected.</summary>
    public static readonly DependencyProperty IsSelectionEnabledProperty = DependencyProperty.RegisterAttached(
        "IsSelectionEnabled", typeof(bool), typeof(SelectableTextBlock),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnIsSelectionEnabledChanged));

    /// <summary>The resource a block's menu is made from: Copy and Select all.</summary>
    internal const string MenuKey = "Message.TextMenu";

    // The block that last took the keyboard, so that only one block shows a selection at a time.
    private static WeakReference<SelectableTextBlock>? _last;

    private object? _editor;
    private ContextMenu? _menu;

    static SelectableTextBlock()
    {
        // Before the editor's own handlers, which would open WPF's light menu: this one opens the Assistant's.
        EventManager.RegisterClassHandler(typeof(SelectableTextBlock), ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening));
        TextEditorAccess.RegisterCommandHandlers(typeof(SelectableTextBlock));
    }

    /// <summary>Gets whether the text blocks inside <paramref name="element"/> let their words be selected.</summary>
    public static bool GetIsSelectionEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsSelectionEnabledProperty);
    }

    /// <summary>Sets whether the text blocks inside <paramref name="element"/> let their words be selected.</summary>
    public static void SetIsSelectionEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsSelectionEnabledProperty, value);
    }

    /// <summary>Whether this block's words can be selected: the view says so, and the editor could be attached.</summary>
    public bool IsSelectable => _editor is not null;

    /// <summary>What is selected in this block, or empty.</summary>
    public string SelectedText => Selection?.Text ?? string.Empty;

    /// <summary>Selects everything in this block.</summary>
    public void SelectAll() => Selection?.Select(ContentStart, ContentEnd);

    /// <summary>Selects nothing in this block.</summary>
    public void ClearSelection() => Selection?.Select(ContentStart, ContentStart);

    /// <summary>The block's selection, when its words can be selected.</summary>
    internal TextSelection? Selection => TextEditorAccess.SelectionOf(_editor);

    /// <inheritdoc/>
    /// <remarks>A selection is one block's: when another block is clicked into, what was selected in the last one is let go of.</remarks>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_editor is null)
        {
            return;
        }

        if (_last is not null && _last.TryGetTarget(out var previous) && !ReferenceEquals(previous, this))
        {
            previous.ClearSelection();
        }

        _last = new WeakReference<SelectableTextBlock>(this);
    }

    private static void OnIsSelectionEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelectableTextBlock block && (bool)e.NewValue)
        {
            block.EnableSelection();
        }
    }

    // Once: a block that could be selected in stays so (it is not moved between views).
    private void EnableSelection()
    {
        if (_editor is not null)
        {
            return;
        }

        _editor = TextEditorAccess.Attach(this);
        if (_editor is null)
        {
            return;
        }

        // It takes the keyboard when it is clicked, for Ctrl+C, but Tab does not stop at every paragraph, and no rectangle is drawn around it.
        Focusable = true;
        FocusVisualStyle = null;
        KeyboardNavigation.SetIsTabStop(this, false);

        // What is selected stays marked while the menu is open and when another window is clicked.
        SetValue(TextBoxBase.IsInactiveSelectionHighlightEnabledProperty, true);
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not SelectableTextBlock { _editor: not null } block)
        {
            return;
        }

        // The block's own menu, where the pointer is; the editor's is never made.
        e.Handled = true;
        block._menu ??= block.TryFindResource(MenuKey) as ContextMenu;
        if (block._menu is { } menu)
        {
            block.Focus();
            menu.PlacementTarget = block;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// The editor of WPF's text controls, for a text block. Everything here is internal to WPF and is looked up once; if any of it is not found (another
    /// version of WPF), nothing is attached.
    /// </summary>
    private static class TextEditorAccess
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Type? Editor = typeof(TextBlock).Assembly.GetType("System.Windows.Documents.TextEditor");
        private static readonly Type? Container = typeof(TextBlock).Assembly.GetType("System.Windows.Documents.ITextContainer");
        private static readonly PropertyInfo? IsReadOnly = Editor?.GetProperty("IsReadOnly", Hidden);
        private static readonly PropertyInfo? EditorView = Editor?.GetProperty("TextView", Hidden);
        private static readonly PropertyInfo? EditorSelection = Editor?.GetProperty("Selection", Hidden);
        private static readonly PropertyInfo? ContainerView = Container?.GetProperty("TextView");
        private static readonly PropertyInfo? BlockContainer = typeof(TextBlock).GetProperty("TextContainer", Hidden);
        private static readonly MethodInfo? Register = Editor?.GetMethod(
            "RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(Type), typeof(bool), typeof(bool), typeof(bool)], null);

        private static bool IsAvailable =>
            Editor is not null && IsReadOnly is not null && EditorView is not null && EditorSelection is not null && ContainerView is not null && BlockContainer is not null
            && Register is not null;

        // Plain text only, read-only, and with the editor's handlers for the mouse and the keyboard.
        public static void RegisterCommandHandlers(Type control)
        {
            if (!IsAvailable)
            {
                return;
            }

            try
            {
                Register!.Invoke(null, [control, false, true, true]);
            }
            catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or MemberAccessException)
            {
                // Without them nothing can be selected; the blocks are text blocks as before.
            }
        }

        public static object? Attach(TextBlock block)
        {
            if (!IsAvailable)
            {
                return null;
            }

            try
            {
                var container = BlockContainer!.GetValue(block);
                var editor = Activator.CreateInstance(
                    Editor!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.CreateInstance, null, [container, block, false], null);
                IsReadOnly!.SetValue(editor, true);
                EditorView!.SetValue(editor, ContainerView!.GetValue(container));
                return editor;
            }
            catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or MemberAccessException or MissingMethodException or InvalidOperationException)
            {
                return null;
            }
        }

        public static TextSelection? SelectionOf(object? editor)
        {
            try
            {
                return editor is null ? null : EditorSelection?.GetValue(editor) as TextSelection;
            }
            catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or MemberAccessException)
            {
                return null;
            }
        }
    }
}
