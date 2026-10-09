using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A scroll viewer whose content fades away toward its top and bottom edges rather than stopping at them, so it can
/// pass under controls that float over it and dissolve into the surface beneath, as in the floating conversation
/// reference. Each fade is a list of points, each a distance from its edge in DIPs and the content's opacity there;
/// opacity runs straight from point to point and keeps the nearest point's value beyond them. Page Up and Page Down
/// move by the clear height between the fades, so no line is passed over while it is faded.
/// <para>
/// The wheel moves the content as far as it was turned: a notch of a mouse wheel moves it <see cref="NotchDistance"/> DIPs, and the many small
/// movements a touchpad sends move it each a little, where a plain scroll viewer takes every one of them for a whole notch (which made a touchpad
/// fling the conversation about). The content answers at once and glides the rest of the way. A scroller inside the content (a long question's
/// details, the steps of a run) takes the wheel only while it has somewhere to go that way; otherwise the wheel is the conversation's, so the
/// pointer resting on a card never stops the scrolling.
/// </para>
/// </summary>
public sealed class FadingScrollViewer : ScrollViewer
{
    /// <summary>Identifies the <see cref="TopFade"/> property.</summary>
    public static readonly DependencyProperty TopFadeProperty = DependencyProperty.Register(
        nameof(TopFade), typeof(PointCollection), typeof(FadingScrollViewer), new PropertyMetadata(null, OnFadeChanged));

    /// <summary>Identifies the <see cref="BottomFade"/> property.</summary>
    public static readonly DependencyProperty BottomFadeProperty = DependencyProperty.Register(
        nameof(BottomFade), typeof(PointCollection), typeof(FadingScrollViewer), new PropertyMetadata(null, OnFadeChanged));

    /// <summary>How far one notch of a mouse wheel moves the content, in DIPs.</summary>
    public const double NotchDistance = 56;

    // How much of the way the content goes in the frame the wheel turned, and how quickly it covers the rest (a time constant, in seconds).
    private const double FirstStep = 0.3;
    private const double GlideTime = 0.055;

    private double _target;
    private double _position;
    private bool _gliding;
    private long _lastFrame;

    // Paging commands: the Page Up and Page Down keys' own, and a scroll bar's. These bindings come before the base
    // class's, which page by the whole viewport.
    static FadingScrollViewer()
    {
        RegisterPaging(ComponentCommands.ScrollPageUp, down: false);
        RegisterPaging(ComponentCommands.ScrollPageDown, down: true);
        RegisterPaging(ScrollBar.PageUpCommand, down: false);
        RegisterPaging(ScrollBar.PageDownCommand, down: true);
    }

    /// <summary>How the content fades toward the top edge: distances from it, in DIPs, with the opacity at each.</summary>
    public PointCollection? TopFade
    {
        get => (PointCollection?)GetValue(TopFadeProperty);
        set => SetValue(TopFadeProperty, value);
    }

    /// <summary>How the content fades toward the bottom edge: distances from it, in DIPs, with the opacity at each.</summary>
    public PointCollection? BottomFade
    {
        get => (PointCollection?)GetValue(BottomFadeProperty);
        set => SetValue(BottomFadeProperty, value);
    }

    /// <summary>
    /// Gets the content's opacity <paramref name="y"/> DIPs below the top of a viewer <paramref name="height"/> tall:
    /// the lower of what its two fades allow there.
    /// </summary>
    public static double GetOpacity(double y, double height, IEnumerable<Point>? top, IEnumerable<Point>? bottom) =>
        Math.Min(Along(top, y), Along(bottom, height - y));

    /// <summary>
    /// Creates the opacity mask for a viewer <paramref name="height"/> tall, or null if nothing fades: a vertical
    /// gradient with a stop at every point of either fade.
    /// </summary>
    public static Brush? CreateMask(double height, IEnumerable<Point>? top, IEnumerable<Point>? bottom)
    {
        if (!(height > 0) || (top?.Any() != true && bottom?.Any() != true))
        {
            return null;
        }

        var positions = (top ?? []).Select(point => point.X)
            .Concat((bottom ?? []).Select(point => height - point.X))
            .Select(y => Math.Clamp(y, 0, height))
            .Append(0).Append(height).Distinct().Order();
        var stops = new GradientStopCollection(positions.Select(y => new GradientStop(
            Color.FromArgb((byte)Math.Round(255 * GetOpacity(y, height, top, bottom)), 0, 0, 0), y / height)));
        var mask = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, height))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        mask.Freeze();
        return mask;
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMask();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // With Shift or Ctrl the keys are not the commands' gestures, so they arrive here; the base class ignores Alt.
        if (!e.Handled && e.Key is Key.PageUp or Key.PageDown && (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == 0)
        {
            Page(e.Key == Key.PageDown);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || e.Delta == 0 || ScrollableHeight <= 0 || InnerScrollerTakes(e))
        {
            return;
        }

        e.Handled = true;
        if (!_gliding)
        {
            _position = VerticalOffset;
            _target = _position;
        }

        _target = Math.Clamp(_target - (e.Delta / 120.0 * NotchDistance), 0, ScrollableHeight);
        if (!SystemParameters.ClientAreaAnimation)
        {
            StopGlide();
            ScrollToVerticalOffset(_target);
            return;
        }

        // Part of the way at once, so the content moves in the frame the wheel did; the rest follows over the next few.
        _position += (_target - _position) * FirstStep;
        ScrollToVerticalOffset(_position);
        if (!_gliding)
        {
            _gliding = true;
            _lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnGlide;
        }
    }

    /// <inheritdoc/>
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);

        // What is scrolled grew or shrank under the glide (an answer streaming in, which is followed to its end): the glide gives way to it.
        if (_gliding && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
        {
            StopGlide();
        }
    }

    private void OnGlide(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        _target = Math.Clamp(_target, 0, ScrollableHeight);
        var left = _target - _position;
        if (Math.Abs(left) < 0.5 || !IsVisible)
        {
            ScrollToVerticalOffset(_target);
            StopGlide();
            return;
        }

        _position += left * (1 - Math.Exp(-Math.Clamp(elapsed, 0, 0.1) / GlideTime));
        ScrollToVerticalOffset(_position);
    }

    private void StopGlide()
    {
        if (_gliding)
        {
            _gliding = false;
            CompositionTarget.Rendering -= OnGlide;
        }
    }

    // Whether a scroller inside the content, under the pointer, has somewhere to go the way the wheel turned: then the wheel is its own.
    private bool InnerScrollerTakes(MouseWheelEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, this); element = ParentOf(element))
        {
            if (element is ScrollViewer { ScrollableHeight: > 0 } inner
                && (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    private static void OnFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FadingScrollViewer)d).UpdateMask();

    private void UpdateMask() => OpacityMask = CreateMask(RenderSize.Height, TopFade, BottomFade);

    private static void RegisterPaging(RoutedCommand command, bool down) =>
        CommandManager.RegisterClassCommandBinding(typeof(FadingScrollViewer), new CommandBinding(command,
            (sender, _) => ((FadingScrollViewer)sender).Page(down), (_, e) => e.CanExecute = true));

    // A page is the clear height between the fades, or the whole viewport if they leave none.
    private void Page(bool down)
    {
        var clear = ViewportHeight - Reach(TopFade) - Reach(BottomFade);
        var page = clear > 0 ? clear : ViewportHeight;
        ScrollToVerticalOffset(VerticalOffset + (down ? page : -page));
    }

    // The opacity a fade allows distance DIPs from its edge; content without a fade is opaque.
    private static double Along(IEnumerable<Point>? fade, double distance)
    {
        var points = fade?.OrderBy(point => point.X).ToArray() ?? [];
        if (points.Length == 0)
        {
            return 1;
        }

        if (distance <= points[0].X)
        {
            return OpacityOf(points[0]);
        }

        for (var i = 1; i < points.Length; i++)
        {
            if (distance <= points[i].X)
            {
                var (from, to) = (points[i - 1], points[i]);
                return OpacityOf(from) + (OpacityOf(to) - OpacityOf(from)) * (distance - from.X) / (to.X - from.X);
            }
        }

        return OpacityOf(points[^1]);
    }

    // How far a fade reaches from its edge.
    private static double Reach(IEnumerable<Point>? fade) => fade?.Select(point => point.X).DefaultIfEmpty().Max() ?? 0;

    private static double OpacityOf(Point point) => Math.Clamp(point.Y, 0, 1);
}
