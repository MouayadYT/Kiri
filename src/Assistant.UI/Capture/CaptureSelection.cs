using System.Windows;

namespace Assistant.UI.Capture;

/// <summary>Where the pointer is on a <see cref="CaptureSelection"/>: off it, inside it, or on one of its handles or edges.</summary>
internal enum SelectionPart
{
    /// <summary>Not on the selection, or there is none.</summary>
    Outside,

    /// <summary>Inside it: dragging moves it.</summary>
    Inside,

    /// <summary>A corner handle.</summary>
    TopLeft,

    /// <summary>The top edge, or its handle.</summary>
    Top,

    /// <summary>A corner handle.</summary>
    TopRight,

    /// <summary>The right edge, or its handle.</summary>
    Right,

    /// <summary>A corner handle.</summary>
    BottomRight,

    /// <summary>The bottom edge, or its handle.</summary>
    Bottom,

    /// <summary>A corner handle.</summary>
    BottomLeft,

    /// <summary>The left edge, or its handle.</summary>
    Left,
}

/// <summary>
/// The rectangle the user draws on one monitor of the Visual Intelligence overlay (PROJECT_SPEC §4.6) and adjusts afterwards, in
/// device-independent pixels of the overlay window: drag on the background to draw one, drag a handle or an edge to resize it, drag
/// inside it to move it. It always stays inside the monitor. It holds no pixels and knows nothing of windows, so what is drawn
/// and what the pointer does can be tested alone.
/// </summary>
internal sealed class CaptureSelection
{
    /// <summary>The smallest a drawn selection may be, on each side: a drag that ends smaller was a click, and leaves none.</summary>
    public const double MinSize = 10;

    /// <summary>How near a handle's center the pointer must be to grab it: more than the handle is drawn, so it is easy to hit.</summary>
    public const double HandleHitRadius = 9;

    /// <summary>How near an edge the pointer must be to grab it, on either side of it.</summary>
    public const double EdgeHitWidth = 5;

    /// <summary>The handles in the middle of the sides are drawn only on a side at least this long, so a small selection is not all handles.</summary>
    public const double MidHandleMinSide = 36;

    private Rect _rect = Rect.Empty;
    private Gesture _gesture;
    private SelectionPart _part;
    private Point _anchor;
    private Vector _grab;
    private Rect _before;

    /// <summary>Creates an empty selection on a monitor of <paramref name="bounds"/>.</summary>
    public CaptureSelection(Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The monitor has a size.");
        }

        Bounds = bounds;
    }

    private enum Gesture
    {
        None,
        Creating,
        Moving,
        Resizing,
    }

    /// <summary>Raised whenever the rectangle changes, while it is dragged too.</summary>
    public event EventHandler? Changed;

    /// <summary>The monitor's size: the selection never leaves it.</summary>
    public Size Bounds { get; }

    /// <summary>Whether there is a selection to show.</summary>
    public bool HasSelection => !_rect.IsEmpty && _rect.Width > 0 && _rect.Height > 0;

    /// <summary>The selection, or <see cref="Rect.Empty"/> when there is none.</summary>
    public Rect Rect => HasSelection ? _rect : Rect.Empty;

    /// <summary>Whether a drag is on: the pointer is down and has not been released.</summary>
    public bool IsDragging => _gesture != Gesture.None;

    /// <summary>What a drag that starts at <paramref name="point"/> would do: which part of the selection it is on.</summary>
    public SelectionPart HitTest(Point point)
    {
        if (!HasSelection)
        {
            return SelectionPart.Outside;
        }

        foreach (var (part, center) in Handles())
        {
            if ((point - center).Length <= HandleHitRadius)
            {
                return part;
            }
        }

        // The edges, along their whole length, with the corners' handles taken first.
        var rect = _rect;
        var withinVertically = point.Y >= rect.Top - EdgeHitWidth && point.Y <= rect.Bottom + EdgeHitWidth;
        var withinHorizontally = point.X >= rect.Left - EdgeHitWidth && point.X <= rect.Right + EdgeHitWidth;
        if (withinVertically && Math.Abs(point.X - rect.Left) <= EdgeHitWidth)
        {
            return SelectionPart.Left;
        }

        if (withinVertically && Math.Abs(point.X - rect.Right) <= EdgeHitWidth)
        {
            return SelectionPart.Right;
        }

        if (withinHorizontally && Math.Abs(point.Y - rect.Top) <= EdgeHitWidth)
        {
            return SelectionPart.Top;
        }

        if (withinHorizontally && Math.Abs(point.Y - rect.Bottom) <= EdgeHitWidth)
        {
            return SelectionPart.Bottom;
        }

        return rect.Contains(point) ? SelectionPart.Inside : SelectionPart.Outside;
    }

    /// <summary>The handles that are shown, with the center of each: the four corners, and the middles of the sides that are long enough.</summary>
    public IEnumerable<(SelectionPart Part, Point Center)> Handles() => HandlesOf(Rect);

    /// <summary>The handles of a selection that is <paramref name="rect"/>, as <see cref="Handles"/> lists them; none for an empty one.</summary>
    public static IEnumerable<(SelectionPart Part, Point Center)> HandlesOf(Rect rect)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            yield break;
        }

        var middleX = rect.Left + rect.Width / 2;
        var middleY = rect.Top + rect.Height / 2;
        yield return (SelectionPart.TopLeft, new Point(rect.Left, rect.Top));
        yield return (SelectionPart.TopRight, new Point(rect.Right, rect.Top));
        yield return (SelectionPart.BottomRight, new Point(rect.Right, rect.Bottom));
        yield return (SelectionPart.BottomLeft, new Point(rect.Left, rect.Bottom));
        if (rect.Width >= MidHandleMinSide)
        {
            yield return (SelectionPart.Top, new Point(middleX, rect.Top));
            yield return (SelectionPart.Bottom, new Point(middleX, rect.Bottom));
        }

        if (rect.Height >= MidHandleMinSide)
        {
            yield return (SelectionPart.Left, new Point(rect.Left, middleY));
            yield return (SelectionPart.Right, new Point(rect.Right, middleY));
        }
    }

    /// <summary>
    /// Starts a drag where the pointer went down: on a handle or an edge it resizes the selection, inside it moves it, and anywhere
    /// else it starts drawing a new one, taking the old one away.
    /// </summary>
    public void Begin(Point point)
    {
        point = Clamp(point);
        _part = HitTest(point);
        _before = _rect;
        switch (_part)
        {
            case SelectionPart.Outside:
                _gesture = Gesture.Creating;
                _anchor = point;
                SetRect(Rect.Empty);
                break;
            case SelectionPart.Inside:
                _gesture = Gesture.Moving;
                _grab = point - _rect.TopLeft;
                break;
            default:
                _gesture = Gesture.Resizing;
                break;
        }
    }

    /// <summary>Carries the drag on to where the pointer is now.</summary>
    public void Drag(Point point)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        point = Clamp(point);
        switch (_gesture)
        {
            case Gesture.Creating:
                SetRect(new Rect(_anchor, point));
                break;
            case Gesture.Moving:
                var topLeft = new Point(
                    Math.Clamp(point.X - _grab.X, 0, Bounds.Width - _before.Width),
                    Math.Clamp(point.Y - _grab.Y, 0, Bounds.Height - _before.Height));
                SetRect(new Rect(topLeft, _before.Size));
                break;
            default:
                SetRect(Resized(point));
                break;
        }
    }

    /// <summary>
    /// Ends the drag where the pointer was released. A selection drawn smaller than <see cref="MinSize"/> was only a click, and is
    /// taken away.
    /// </summary>
    /// <returns>Whether there is a selection now.</returns>
    public bool End(Point point)
    {
        Drag(point);
        var drawing = _gesture == Gesture.Creating;
        _gesture = Gesture.None;
        if (drawing && HasSelection && (_rect.Width < MinSize || _rect.Height < MinSize))
        {
            SetRect(Rect.Empty);
        }

        return HasSelection;
    }

    /// <summary>Takes the selection away, and ends any drag.</summary>
    public void Clear()
    {
        _gesture = Gesture.None;
        SetRect(Rect.Empty);
    }

    /// <summary>Selects <paramref name="area"/>, cut to the monitor.</summary>
    public void Select(Rect area)
    {
        _gesture = Gesture.None;
        area.Intersect(new Rect(Bounds));
        SetRect(area);
    }

    // The selection with the edges the dragged part touches moved to the pointer; dragging an edge past the opposite one turns it over.
    private Rect Resized(Point point)
    {
        var (left, top, right, bottom) = (_before.Left, _before.Top, _before.Right, _before.Bottom);
        if (_part is SelectionPart.TopLeft or SelectionPart.Left or SelectionPart.BottomLeft)
        {
            left = point.X;
        }

        if (_part is SelectionPart.TopRight or SelectionPart.Right or SelectionPart.BottomRight)
        {
            right = point.X;
        }

        if (_part is SelectionPart.TopLeft or SelectionPart.Top or SelectionPart.TopRight)
        {
            top = point.Y;
        }

        if (_part is SelectionPart.BottomLeft or SelectionPart.Bottom or SelectionPart.BottomRight)
        {
            bottom = point.Y;
        }

        return new Rect(new Point(Math.Min(left, right), Math.Min(top, bottom)), new Point(Math.Max(left, right), Math.Max(top, bottom)));
    }

    private Point Clamp(Point point) =>
        new(Math.Clamp(point.X, 0, Bounds.Width), Math.Clamp(point.Y, 0, Bounds.Height));

    private void SetRect(Rect rect)
    {
        if (_rect != rect)
        {
            _rect = rect;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
