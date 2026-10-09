using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistant.UI.Capture;

/// <summary>
/// What the Visual Intelligence overlay (PROJECT_SPEC §4.6) draws on one monitor, and the pointer that drives it: the monitor's
/// snapshot, dimmed, with the selection clear of the dimming, a soft white glow around it and a white handle at each corner and the
/// middle of each long side, laid out from the reference. Dragging on the background draws a selection, dragging a handle or an edge
/// resizes it, dragging inside moves it. The selection is the <see cref="CaptureSelection"/> it is given, in the element's own
/// device-independent pixels, and is drawn and cropped on whole device pixels, so what is seen is what is taken.
/// </summary>
internal sealed class CaptureSurface : FrameworkElement
{
    /// <summary>Identifies the <see cref="Snapshot"/> property.</summary>
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(ImageSource), typeof(CaptureSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Selection"/> property.</summary>
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(CaptureSelection), typeof(CaptureSurface),
        new FrameworkPropertyMetadata(null, OnSelectionChanged));

    // What the reference measures, in DIPs (its pixels are 2x). Outside the selection the screen is black at 50 %: white at
    // 255 reads 128 there.
    private const double DimOpacity = 0.5;

    // A handle is a white disc 6 DIPs across (12 pixels in the reference), with a faint dark rim so it still shows on a white page.
    private const double HandleDiameter = 6;

    // How far the glow reaches outside the selection's edge, and the step the rings that draw it are laid in.
    private const double GlowReach = 5;
    private const double GlowStep = 0.25;

    // The glow's opacity at a distance outside the edge, measured across the reference's left edge (at 0.25 DIP, 166 on a screen of 128,
    // which is 0.30 of the way to white), falling to nothing by 4.5.
    private static readonly (double Distance, double Opacity)[] GlowProfile =
    [
        (0, 0.34), (0.375, 0.30), (0.875, 0.197), (1.375, 0.102), (1.875, 0.055), (2.375, 0.031), (2.875, 0.016), (3.375, 0.016),
        (3.875, 0.008), (GlowReach, 0),
    ];

    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(DimOpacity * 255), 0, 0, 0)));
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Pen HandleRim = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.5));
    private static readonly Brush[] GlowBrushes = CreateGlowBrushes();

    static CaptureSurface()
    {
        FocusableProperty.OverrideMetadata(typeof(CaptureSurface), new FrameworkPropertyMetadata(true));
        CursorProperty.OverrideMetadata(typeof(CaptureSurface), new FrameworkPropertyMetadata(Cursors.Cross));
    }

    /// <summary>Raised when the user presses on the surface to draw, move or resize the selection: the other monitors' selections go.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Raised while the selection changes, as it is dragged and when it is set.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the user lets go: <see cref="CaptureSelection.HasSelection"/> says whether a selection stands.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Raised when the user presses the right button: the selection goes, or, with none, the whole capture is given up.</summary>
    public event EventHandler? SecondaryClicked;

    /// <summary>The monitor's snapshot: one device pixel for each of the monitor's, at 96 DPI, so the element draws it one for one.</summary>
    public ImageSource? Snapshot
    {
        get => (ImageSource?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    /// <summary>The selection drawn here and changed by the pointer.</summary>
    public CaptureSelection? Selection
    {
        get => (CaptureSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>
    /// The selection in device pixels of the monitor: its edges rounded to whole pixels, which is what is drawn and what the crop of the
    /// snapshot takes. <see cref="Int32Rect.Empty"/> when there is none.
    /// </summary>
    public Int32Rect PixelSelection
    {
        get
        {
            if (Selection is not { HasSelection: true } selection)
            {
                return Int32Rect.Empty;
            }

            var (scaleX, scaleY) = DeviceScale;
            var rect = selection.Rect;
            var left = Pixel(rect.Left * scaleX);
            var top = Pixel(rect.Top * scaleY);
            var right = Pixel(rect.Right * scaleX);
            var bottom = Pixel(rect.Bottom * scaleY);
            return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
        }
    }

    /// <summary>The selection on whole device pixels, in the element's device-independent pixels: what is drawn.</summary>
    public Rect SnappedSelection
    {
        get
        {
            var pixels = PixelSelection;
            if (pixels.IsEmpty)
            {
                return Rect.Empty;
            }

            var (scaleX, scaleY) = DeviceScale;
            return new Rect(pixels.X / scaleX, pixels.Y / scaleY, pixels.Width / scaleX, pixels.Height / scaleY);
        }
    }

    /// <summary>
    /// Device pixels for each device-independent pixel, which the selection is snapped to: the DPI of the window's monitor. A
    /// fixture that is drawn at the reference's 2x whatever the display is gives it here; in the app nothing does.
    /// </summary>
    internal double? DeviceScaleOverride { get; set; }

    private (double X, double Y) DeviceScale
    {
        get
        {
            if (DeviceScaleOverride is { } scale)
            {
                return (scale, scale);
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(RenderSize);
        if (Snapshot is { } snapshot)
        {
            drawingContext.DrawImage(snapshot, bounds);
        }

        drawingContext.DrawRectangle(Dim, null, bounds);
        var rect = SnappedSelection;
        if (rect.IsEmpty)
        {
            return;
        }

        DrawGlow(drawingContext, rect);

        // The selection is the snapshot again, undimmed.
        if (Snapshot is { } clear)
        {
            drawingContext.PushClip(new RectangleGeometry(rect));
            drawingContext.DrawImage(clear, bounds);
            drawingContext.Pop();
        }
        else
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, rect);
        }

        // The handles sit on the selection's edge as it is drawn, on whole pixels.
        foreach (var (_, center) in CaptureSelection.HandlesOf(rect))
        {
            drawingContext.DrawEllipse(HandleFill, HandleRim, center, HandleDiameter / 2, HandleDiameter / 2);
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (PointerDown(e.GetPosition(this)))
        {
            Focus();
            CaptureMouse();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMoved(e.GetPosition(this));
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (PointerUp(e.GetPosition(this)))
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        SecondaryClicked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture taken away in the middle of a drag (another window came forward): the drag ends where it is.
        if (Selection is { IsDragging: true } selection)
        {
            selection.End(e.GetPosition(this));
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The left button goes down at <paramref name="point"/>: a drag starts. Returns whether it did.</summary>
    internal bool PointerDown(Point point)
    {
        if (Selection is not { } selection)
        {
            return false;
        }

        DragStarted?.Invoke(this, EventArgs.Empty);
        selection.Begin(point);
        return true;
    }

    /// <summary>The pointer moves to <paramref name="point"/>: the drag goes on, or, with none, the cursor says what a drag would do.</summary>
    internal void PointerMoved(Point point)
    {
        if (Selection is not { } selection)
        {
            return;
        }

        if (selection.IsDragging)
        {
            selection.Drag(point);
        }
        else
        {
            Cursor = CursorFor(selection.HitTest(point));
        }
    }

    /// <summary>The left button is released at <paramref name="point"/>: the drag ends. Returns whether one was on.</summary>
    internal bool PointerUp(Point point)
    {
        if (Selection is not { IsDragging: true } selection)
        {
            return false;
        }

        selection.End(point);
        Cursor = CursorFor(selection.HitTest(point));
        DragCompleted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // A device-independent position on the nearest whole pixel, halves rounded up, so that 62.5 is pixel 63 on every run.
    private static int Pixel(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    internal static Cursor CursorFor(SelectionPart part) => part switch
    {
        SelectionPart.Inside => Cursors.SizeAll,
        SelectionPart.TopLeft or SelectionPart.BottomRight => Cursors.SizeNWSE,
        SelectionPart.TopRight or SelectionPart.BottomLeft => Cursors.SizeNESW,
        SelectionPart.Top or SelectionPart.Bottom => Cursors.SizeNS,
        SelectionPart.Left or SelectionPart.Right => Cursors.SizeWE,
        _ => Cursors.Cross,
    };

    // The glow is rings laid one against the next outside the selection's edge, each with the opacity the reference has at its distance.
    private static void DrawGlow(DrawingContext drawingContext, Rect selection)
    {
        for (var ring = 0; ring < GlowBrushes.Length; ring++)
        {
            var distance = ring * GlowStep;
            var middle = distance + GlowStep / 2;
            var path = selection;
            path.Inflate(middle, middle);
            drawingContext.DrawRoundedRectangle(
                null, new Pen(GlowBrushes[ring], GlowStep), path, middle, middle);
        }
    }

    private static void OnSelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var surface = (CaptureSurface)target;
        if (e.OldValue is CaptureSelection old)
        {
            old.Changed -= surface.OnSelectionRectChanged;
        }

        if (e.NewValue is CaptureSelection selection)
        {
            selection.Changed += surface.OnSelectionRectChanged;
        }

        surface.OnSelectionRectChanged(surface, EventArgs.Empty);
    }

    private void OnSelectionRectChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Brush[] CreateGlowBrushes()
    {
        var rings = (int)Math.Round(GlowReach / GlowStep);
        var brushes = new Brush[rings];
        for (var ring = 0; ring < rings; ring++)
        {
            var opacity = Interpolate(ring * GlowStep + GlowStep / 2);
            brushes[ring] = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255)));
        }

        return brushes;
    }

    private static double Interpolate(double distance)
    {
        for (var index = 1; index < GlowProfile.Length; index++)
        {
            var (to, toOpacity) = GlowProfile[index];
            if (distance <= to)
            {
                var (from, fromOpacity) = GlowProfile[index - 1];
                return fromOpacity + (toOpacity - fromOpacity) * (distance - from) / (to - from);
            }
        }

        return 0;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
