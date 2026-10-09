using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistant.UI.Windowing;

/// <summary>
/// Lets the user move a borderless window by dragging its glass, as with a title bar, while controls and content keep
/// their own mouse handling: a press on an editor, button, scroll bar, list, link or any content the window names never
/// starts a drag. A press becomes a drag only once the pointer moves past the system's drag distance, so a click on the
/// glass still does what it did.
/// </summary>
internal sealed class SurfaceDrag
{
    private readonly Window _window;
    private readonly Func<DependencyObject, bool> _isContent;
    private Point? _pressedAt;

    private SurfaceDrag(Window window, Func<DependencyObject, bool> isContent)
    {
        _window = window;
        _isContent = isContent;
        window.PreviewMouseLeftButtonDown += OnPressed;
        window.PreviewMouseMove += OnMoved;
        window.PreviewMouseLeftButtonUp += (_, _) => _pressedAt = null;
    }

    /// <summary>Raised after the user has dragged the window somewhere else.</summary>
    public event EventHandler? Dragged;

    /// <summary>
    /// Makes <paramref name="window"/> draggable by its glass. <paramref name="isContent"/> names further elements,
    /// such as a conversation's messages, that a drag must never start from.
    /// </summary>
    public static SurfaceDrag Attach(Window window, Func<DependencyObject, bool>? isContent = null) =>
        new(window, isContent ?? (_ => false));

    /// <summary>
    /// Whether a press on <paramref name="source"/> may move the window: it is not inside a control that handles the
    /// mouse itself, nor inside content that <paramref name="isContent"/> names.
    /// </summary>
    public static bool CanDragFrom(DependencyObject? source, Func<DependencyObject, bool> isContent)
    {
        for (var element = source; element is not null; element = ParentOf(element))
        {
            if (element is TextBoxBase or PasswordBox or ButtonBase or RangeBase or Thumb or Selector or Hyperlink ||
                isContent(element))
            {
                return false;
            }
        }

        return source is not null;
    }

    private void OnPressed(object sender, MouseButtonEventArgs e) =>
        _pressedAt = CanDragFrom(e.OriginalSource as DependencyObject, _isContent) ? e.GetPosition(_window) : null;

    private void OnMoved(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } pressedAt)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || (Mouse.Captured is { } captured && captured != _window))
        {
            _pressedAt = null;
            return;
        }

        var moved = e.GetPosition(_window) - pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedAt = null;
        _window.DragMove();
        Dragged?.Invoke(this, EventArgs.Empty);
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(element),
    };
}
