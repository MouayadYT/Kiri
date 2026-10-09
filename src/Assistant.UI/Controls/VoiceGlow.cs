using System.Windows;
using System.Windows.Media;
using Assistant.UI.Animation;
using Assistant.UI.Voice;

namespace Assistant.UI.Controls;

/// <summary>
/// The microphone visualizer: a bright glow along the bottom edge of a surface, shown only while voice input is on.
/// It rises from the edge when voice input starts and rests low while the room is quiet; speech pushes it upward
/// and sets it moving; when the speech stops it settles back down; and when voice input ends it sinks and disappears.
/// Host it over the surface's glass, clipped to the surface's outline.
/// </summary>
/// <remarks>
/// It runs a frame loop only while it is shown or settling, and only while it is visible. With Windows animation
/// effects off it shows the resting glow while voice input is on, without movement (PROJECT_SPEC §4.0).
/// </remarks>
public sealed class VoiceGlow : FrameworkElement
{
    /// <summary>Identifies the <see cref="IsActive"/> property.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(VoiceGlow), new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Identifies the <see cref="Source"/> property.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IVoiceLevelSource), typeof(VoiceGlow), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Shape"/> property.</summary>
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(VoiceGlowShape), typeof(VoiceGlow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Fill"/> property.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(VoiceGlow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // How long the glow takes to rise into view when voice input starts, and to sink away when it ends.
    private static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan DisappearDuration = TimeSpan.FromMilliseconds(420);

    // A frame later than this counts as this long, so a stalled frame slows the glow rather than making it jump.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly PresenceTween _presence = new(0);
    private readonly VoiceEnergy _energy = new();
    private TimeSpan? _lastFrame;
    private bool _running;

    public VoiceGlow() : this(new RenderingFrameSource(), () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal VoiceGlow(IFrameSource frames, Func<bool> animationsEnabled)
    {
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        IsHitTestVisible = false;
        frames.Frame += OnFrame;
        IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>Whether voice input is on, so the glow shows and follows <see cref="Source"/>.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Where the microphone's level is read, once per frame while <see cref="IsActive"/>.</summary>
    public IVoiceLevelSource? Source
    {
        get => (IVoiceLevelSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The glow's shape at rest and with voice.</summary>
    public VoiceGlowShape? Shape
    {
        get => (VoiceGlowShape?)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>
    /// The brush for each ellipse of the glow. A radial gradient mapped to its bounding box fades each ellipse from
    /// its center out to its edge.
    /// </summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>How far the glow has risen into view, from 0 (gone) to 1 (shown).</summary>
    internal double Presence => _presence.Value;

    /// <summary>The voice energy it currently shows, from 0 (at rest) to 1.</summary>
    internal double Energy => _energy.Value;

    /// <summary>How far its motion has advanced.</summary>
    internal double Phase { get; private set; }

    /// <summary>Whether its frame loop is running.</summary>
    internal bool IsAnimating => _running;

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var presence = _presence.Value;
        if (presence <= 0 || Fill is not { } fill || Shape is not { } shape || RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        // Rising into view, the glow grows up from the edge as it fades in.
        drawingContext.PushOpacity(presence);
        foreach (var blob in shape.Layout(RenderSize, _energy.Value, Phase))
        {
            if (blob.RadiusX > 0 && blob.RadiusY > 0)
            {
                drawingContext.DrawEllipse(fill, null, blob.Center, blob.RadiusX, blob.RadiusY * presence);
            }
        }

        drawingContext.Pop();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((VoiceGlow)d).Update();

    // Heads for shown while voice input is on and the glow can be seen, and for gone otherwise.
    private void Update()
    {
        if (!IsVisible)
        {
            // Nothing to see: stop at once, so the glow rises afresh the next time it is shown.
            Stop();
            _presence.JumpTo(0);
            _energy.Reset();
            InvalidateVisual();
            return;
        }

        var target = IsActive ? 1 : 0;
        if (!_animationsEnabled())
        {
            Stop();
            _presence.JumpTo(target);
            _energy.Reset();
            InvalidateVisual();
            return;
        }

        if (_presence.Target != target || (!_presence.IsRunning && _presence.Value != target))
        {
            _presence.Start(target, IsActive ? AppearDuration : DisappearDuration);
        }

        if (IsActive || _presence.Value > 0 || _energy.Value > 0)
        {
            Start();
        }
    }

    private void Start()
    {
        if (!_running)
        {
            _running = true;
            _lastFrame = null;
            _frames.Start();
        }
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
        var elapsed = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > MaxFrameStep ? MaxFrameStep : elapsed;

        var (presence, energy) = (_presence.Value, _energy.Value);
        _presence.Advance(elapsed);
        _energy.Update(IsActive ? Source?.ReadLevel() ?? 0 : 0, elapsed);

        // Louder voice moves the glow faster as well as higher.
        Phase += elapsed.TotalSeconds * (0.6 + (1.6 * _energy.Value));

        if (_presence.Value != presence || _energy.Value != energy || _energy.Value > 0)
        {
            InvalidateVisual();
        }

        if (!IsActive && _presence.Value == 0 && _energy.Value == 0)
        {
            Stop();
        }
    }
}
