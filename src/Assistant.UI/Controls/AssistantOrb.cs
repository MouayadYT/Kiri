using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Assistant.UI.Animation;
using Assistant.UI.Orb;

namespace Assistant.UI.Controls;

/// <summary>
/// The assistant orb: a dark glass sphere over a light, glassy lower half, divided by a bright boundary that curves
/// like a smile, after the reference. It is drawn, never a picture, and its boundary is alive. Listening, it follows
/// the sound: it rises, dips, ripples and stretches as the voice does, louder sound moving it more, and settles back to
/// the reference's shape when the sound stops. Thinking, a slow shimmer passes along it whatever the sound; idle, it
/// only breathes; and on an error it sags and warms to a restrained red.
/// </summary>
/// <remarks>
/// <para>
/// Feed it sound through <see cref="Amplitude"/> (a normalized loudness from 0 to 1, set whenever a new reading is
/// available) or, to have it read once per frame, through <see cref="AmplitudeSource"/>. The voice pipeline uses
/// either, unchanged; <see cref="MockSpeechAmplitude"/> stands in until then. Only a listening orb hears the sound.
/// </para>
/// <para>
/// It runs a frame loop only while it is visible, and slows to about 30 frames a second while it is calm. With
/// Windows animation effects off it shows the reference's pose for its state, without moving (PROJECT_SPEC §4.0). Each
/// frame is a few dozen cached gradients over four small geometries, so it can run continuously.
/// </para>
/// </remarks>
public sealed class AssistantOrb : FrameworkElement
{
    /// <summary>Identifies the <see cref="State"/> property.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(OrbState), typeof(AssistantOrb), new PropertyMetadata(OrbState.Idle, OnStateChanged));

    /// <summary>Identifies the <see cref="Amplitude"/> property.</summary>
    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(
        nameof(Amplitude), typeof(double), typeof(AssistantOrb), new PropertyMetadata(0.0, null, CoerceAmplitude));

    /// <summary>Identifies the <see cref="AmplitudeSource"/> property.</summary>
    public static readonly DependencyProperty AmplitudeSourceProperty = DependencyProperty.Register(
        nameof(AmplitudeSource), typeof(IOrbAmplitudeSource), typeof(AssistantOrb), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Shape"/> property.</summary>
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(OrbShape), typeof(AssistantOrb),
        new FrameworkPropertyMetadata(new OrbShape(), FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    /// <summary>Identifies the <see cref="ShowShadow"/> property.</summary>
    public static readonly DependencyProperty ShowShadowProperty = DependencyProperty.Register(
        nameof(ShowShadow), typeof(bool), typeof(AssistantOrb),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    // The side of the orb when nothing sizes it, in DIPs.
    private const double DefaultSide = 120;

    // A frame later than this counts as this long, so a stalled frame slows the orb rather than making it jump.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    // While calm, the orb is drawn about 30 times a second: nothing it shows then moves quickly.
    private static readonly TimeSpan CalmFrameStep = TimeSpan.FromMilliseconds(30);

    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly OrbPainter _painter = new();
    private OrbMotion _motion;
    private TimeSpan? _lastFrame;
    private bool _running;

    public AssistantOrb() : this(new RenderingFrameSource(), () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal AssistantOrb(IFrameSource frames, Func<bool> animationsEnabled)
    {
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        _motion = new OrbMotion(Shape);
        SnapsToDevicePixels = false;
        frames.Frame += OnFrame;
        IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>What the orb shows it is doing.</summary>
    public OrbState State
    {
        get => (OrbState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// How loud the sound is, normalized from 0 (silence) to 1 (as loud as speech gets), read while the orb is
    /// <see cref="OrbState.Listening"/>. Set it whenever a new reading arrives: the orb smooths it, so readings need not
    /// come at the frame rate. It is ignored while <see cref="AmplitudeSource"/> is set.
    /// </summary>
    public double Amplitude
    {
        get => (double)GetValue(AmplitudeProperty);
        set => SetValue(AmplitudeProperty, value);
    }

    /// <summary>
    /// Where the orb reads the sound's loudness itself, once per frame while listening, instead of being given it
    /// through <see cref="Amplitude"/>.
    /// </summary>
    public IOrbAmplitudeSource? AmplitudeSource
    {
        get => (IOrbAmplitudeSource?)GetValue(AmplitudeSourceProperty);
        set => SetValue(AmplitudeSourceProperty, value);
    }

    /// <summary>The boundary's shape at rest and how it answers sound.</summary>
    public OrbShape Shape
    {
        get => (OrbShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>
    /// Whether the orb casts the soft shadow the reference shows, which falls beyond its bounds, mostly below.
    /// </summary>
    public bool ShowShadow
    {
        get => (bool)GetValue(ShowShadowProperty);
        set => SetValue(ShowShadowProperty, value);
    }

    /// <summary>Its living motion, for tests.</summary>
    internal OrbMotion Motion => _motion;

    /// <summary>Whether its frame loop is running.</summary>
    internal bool IsAnimating => _running;

    /// <summary>How many frames it has moved on by, which is fewer than the frames offered while it is calm.</summary>
    internal int FramesDrawn { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? DefaultSide : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? DefaultSide : availableSize.Height);
        return new Size(side, side);
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new OrbAutomationPeer(this);

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(RenderSize.Width, RenderSize.Height);
        if (side <= 0)
        {
            return;
        }

        // Everything is drawn on a unit orb, radius 1 at the origin, scaled and moved into place.
        var radius = side / 2;
        drawingContext.PushTransform(new MatrixTransform(radius, 0, 0, radius, RenderSize.Width / 2, RenderSize.Height / 2));
        _painter.Paint(drawingContext, _motion, ShowShadow, Math.Clamp((int)(side * 0.4), 32, 96));
        drawingContext.Pop();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AssistantOrb)d).Update();

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var orb = (AssistantOrb)d;
        orb._motion = new OrbMotion((OrbShape)e.NewValue);
        orb.Update();
    }

    private static object CoerceAmplitude(DependencyObject d, object value) =>
        value is double amplitude && !double.IsNaN(amplitude) ? Math.Clamp(amplitude, 0, 1) : 0.0;

    // Runs while the orb can be seen; without animation effects it holds the reference's pose for its state.
    private void Update()
    {
        if (IsVisible && _animationsEnabled())
        {
            if (!_running)
            {
                _running = true;
                _lastFrame = null;
                _frames.Start();
            }

            return;
        }

        if (_running)
        {
            _running = false;
            _frames.Stop();
        }

        _motion.Snap(State);
        InvalidateVisual();
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        if (!_running)
        {
            return;
        }

        // The first frame anchors the clock. While calm, frames are dropped until enough time has passed.
        if (_lastFrame is not { } last)
        {
            _lastFrame = time;
            return;
        }

        var step = time - last;
        var listening = State == OrbState.Listening;
        if (step < TimeSpan.Zero || (!listening && !_motion.IsReacting && step < CalmFrameStep))
        {
            if (step < TimeSpan.Zero)
            {
                _lastFrame = time;
            }

            return;
        }

        _lastFrame = time;
        step = step > MaxFrameStep ? MaxFrameStep : step;

        var amplitude = 0.0;
        if (listening)
        {
            var reading = AmplitudeSource?.ReadAmplitude() ?? Amplitude;
            amplitude = double.IsNaN(reading) ? 0 : Math.Clamp(reading, 0, 1);
        }

        _motion.Update(amplitude, State, step);
        FramesDrawn++;
        InvalidateVisual();
    }

    private sealed class OrbAutomationPeer(AssistantOrb orb) : FrameworkElementAutomationPeer(orb)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(AssistantOrb);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return !string.IsNullOrEmpty(name) ? name : ((AssistantOrb)Owner).State switch
            {
                OrbState.Listening => "Assistant, listening",
                OrbState.Thinking => "Assistant, thinking",
                OrbState.Error => "Assistant, something went wrong",
                _ => "Assistant",
            };
        }
    }
}
