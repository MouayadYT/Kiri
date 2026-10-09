using System.Windows;
using System.Windows.Media;
using Assistant.UI.Animation;

namespace Assistant.UI.Controls;

/// <summary>
/// The Searching indicator: a ring of soft, slightly elongated droplets in glass, white on dark, that reads as turning
/// while nothing in it moves. Each droplet stays where it is, at its own place on the ring and its own angle, and only
/// changes size; a swell of size passes from droplet to droplet around the ring (<see cref="DropletRingMotion"/>).
/// Nothing is rotated, and no droplet travels, blinks or fades.
/// </summary>
/// <remarks>
/// It runs a frame loop only while it is active and visible, and stops the moment either ends. With Windows animation
/// effects off it holds the reference's pose, without moving (PROJECT_SPEC §4.0). Drawing is a handful of cached
/// geometries per frame, so it can run continuously.
/// </remarks>
public sealed class SearchingIndicator : FrameworkElement
{
    /// <summary>Identifies the <see cref="IsActive"/> property.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SearchingIndicator), new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Identifies the <see cref="Motion"/> property.</summary>
    public static readonly DependencyProperty MotionProperty = DependencyProperty.Register(
        nameof(Motion), typeof(DropletRingMotion), typeof(SearchingIndicator),
        new FrameworkPropertyMetadata(new DropletRingMotion(),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Fill"/> property.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SearchingIndicator),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // A frame later than this counts as this long, so a stalled frame slows the ring rather than making it jump.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    // How far toward the fuller end an egg is at its widest, as a fraction of half its length.
    private const double WidestAt = -0.1;

    // The soft light around a droplet, out to this many times its length.
    private const double HaloReach = 1.8;

    private static readonly Brush Halo = CreateHalo();

    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private double? _heldPeak;
    private Geometry? _droplet;
    private double _dropletElongation;
    private TimeSpan _elapsed;
    private TimeSpan? _lastFrame;
    private bool _running;

    public SearchingIndicator() : this(new RenderingFrameSource(), () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal SearchingIndicator(IFrameSource frames, Func<bool> animationsEnabled)
    {
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        frames.Frame += OnFrame;
        IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>Whether something is being searched for, so the ring circulates.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The ring's droplets and how their sizes circulate.</summary>
    public DropletRingMotion Motion
    {
        get => (DropletRingMotion)GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    /// <summary>The droplets' color: white, on the dark glass.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Where the swell of size stands on the ring now, in droplets from the first.</summary>
    internal double Peak => _heldPeak ?? (IsAnimating ? Motion.PeakAt(_elapsed) : Motion.RestPeak);

    /// <summary>Holds the swell at a place on the ring, for a fixture that draws a pose, or lets it go with <see langword="null"/>.</summary>
    internal double? HeldPeak
    {
        get => _heldPeak;
        set
        {
            _heldPeak = value;
            InvalidateVisual();
        }
    }

    /// <summary>The length of each droplet now, along its long axis.</summary>
    internal IReadOnlyList<double> Lengths
    {
        get
        {
            var (motion, peak) = (Motion, Peak);
            return [.. Enumerable.Range(0, motion.Count).Select(i => motion.LengthOf(i, peak))];
        }
    }

    /// <summary>Whether its frame loop is running.</summary>
    internal bool IsAnimating => _running;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Room for the ring and its largest droplets; their glow spills beyond it.
        var motion = Motion;
        var side = 2 * (motion.RingRadius + (motion.PeakLength / 2));
        return new Size(Math.Ceiling(side), Math.Ceiling(side));
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Fill is not { } fill || RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        var (motion, peak) = (Motion, Peak);
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        // The droplet is drawn once and scaled into place: a unit-long egg lying along the x axis about the origin.
        if (_droplet is null || _dropletElongation != motion.Elongation)
        {
            _droplet = CreateDroplet(1, motion.Elongation, WidestAt);
            _dropletElongation = motion.Elongation;
        }

        for (var i = 0; i < motion.Count; i++)
        {
            var length = motion.LengthOf(i, peak);
            var angle = motion.AngleOf(i);
            var radians = angle * Math.PI / 180;

            // Each droplet lies along the ring: its long axis is the ring's tangent where it stands.
            var matrix = Matrix.Identity;
            matrix.Scale(length, length);
            matrix.Rotate(angle);
            matrix.Translate(
                center.X + (motion.RingRadius * Math.Sin(radians)),
                center.Y - (motion.RingRadius * Math.Cos(radians)));
            var transform = new MatrixTransform(matrix);
            transform.Freeze();

            drawingContext.PushTransform(transform);
            drawingContext.DrawEllipse(Halo, null, new Point(0, 0), HaloReach / 2, HaloReach / 2);
            drawingContext.DrawGeometry(fill, null, _droplet);
            drawingContext.Pop();
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SearchingIndicator)d).Update();

    // Runs while there is something to search for and the ring can be seen; without animation effects it holds a pose.
    private void Update()
    {
        if (IsActive && IsVisible && _animationsEnabled())
        {
            if (!_running)
            {
                _running = true;
                _lastFrame = null;
                _frames.Start();
            }
        }
        else
        {
            Stop();
        }

        InvalidateVisual();
    }

    private void Stop()
    {
        if (_running)
        {
            _running = false;
            _frames.Stop();
        }
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        if (!_running)
        {
            return;
        }

        // The first frame anchors the clock.
        var step = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        _elapsed += step < TimeSpan.Zero ? TimeSpan.Zero : step > MaxFrameStep ? MaxFrameStep : step;
        InvalidateVisual();
    }

    // An egg of the given long-axis length lying along x about the origin: widest a little toward the -x end, where
    // it is fuller, and narrower toward +x, the end that leads when it is turned to run clockwise around the ring.
    private static Geometry CreateDroplet(double length, double elongation, double widestAt)
    {
        const double quadrant = 0.5523;
        var a = length / 2;
        var b = a / elongation;
        var t = widestAt * a;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(a, 0), isFilled: true, isClosed: true);
            context.BezierTo(new Point(a, -quadrant * b), new Point(t + (quadrant * (a - t)), -b), new Point(t, -b), true, true);
            context.BezierTo(new Point(t - (quadrant * (t + a)), -b), new Point(-a, -quadrant * b), new Point(-a, 0), true, true);
            context.BezierTo(new Point(-a, quadrant * b), new Point(t - (quadrant * (t + a)), b), new Point(t, b), true, true);
            context.BezierTo(new Point(t + (quadrant * (a - t)), b), new Point(a, quadrant * b), new Point(a, 0), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    // White at the droplet's edge fading to nothing at HaloReach, mapped to the halo ellipse's bounds.
    private static Brush CreateHalo()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 255, 255, 255), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 255, 255, 255), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0.68));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1.0));
        brush.Freeze();
        return brush;
    }
}
