using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Animation;

/// <summary>
/// Shows and hides a window with its surface animated by a <see cref="SurfaceMotion"/>. Either call may interrupt the
/// other: the surface turns around from wherever it is. Animation is skipped while Windows animation effects are off
/// (PROJECT_SPEC §4.0), and the window then simply shows or hides.
/// </summary>
/// <remarks>
/// The transition owns the surface's <see cref="UIElement.Opacity"/>, <see cref="UIElement.RenderTransform"/> and
/// <see cref="UIElement.RenderTransformOrigin"/>. Between transitions the surface rests fully shown, so the window
/// also looks right when it is shown or hidden directly.
/// </remarks>
internal sealed class SurfaceTransition
{
    // A frame later than this counts as this long, so a stalled frame slows the animation rather than skipping it.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private readonly Window _window;
    private readonly UIElement _surface;
    private readonly SurfaceMotion _motion;
    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _offset = new();
    private readonly PresenceTween _presence = new(1);
    private TimeSpan? _lastFrame;
    private bool _closed;

    public SurfaceTransition(Window window, UIElement surface, SurfaceMotion motion)
        : this(window, surface, motion, new RenderingFrameSource(), () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal SurfaceTransition(
        Window window, UIElement surface, SurfaceMotion motion, IFrameSource frames, Func<bool> animationsEnabled)
    {
        _window = window;
        _surface = surface;
        _motion = motion;
        _frames = frames;
        _animationsEnabled = animationsEnabled;

        surface.RenderTransformOrigin = new Point(0.5, 0.5);
        surface.RenderTransform = new TransformGroup { Children = { _scale, _offset } };
        frames.Frame += OnFrame;
        window.IsVisibleChanged += OnIsVisibleChanged;
        window.Closed += OnClosed;
    }

    /// <summary>Raised whenever the surface's appearance changes, with how it is now drawn.</summary>
    public event EventHandler<SurfaceFrame>? FrameApplied;

    /// <summary>Whether the window is visible but on its way out.</summary>
    public bool IsHiding => _window.IsVisible && _presence.Target == 0;

    /// <summary>
    /// Shows the window if it is hidden and brings its surface in, or turns a hide in progress around. The window is
    /// visible when this returns.
    /// </summary>
    public void Show()
    {
        if (_closed)
        {
            return;
        }

        if (_window.IsVisible)
        {
            MoveTo(1, _motion.ShowDuration);
            return;
        }

        // Start from hidden, and already heading in, before the window appears: its first frame is part of the
        // animation, and anything the window raises while showing sees it arriving.
        _presence.JumpTo(0);
        Apply();
        MoveTo(1, _motion.ShowDuration);
        _window.Show();
    }

    /// <summary>Takes the surface out and then hides the window, or turns a show in progress around.</summary>
    public void Hide()
    {
        if (!_closed && _window.IsVisible)
        {
            MoveTo(0, _motion.HideDuration);
        }
    }

    /// <summary>Takes the surface out and hides the window at once, with no animation, as when the screen is about to be captured.</summary>
    public void HideNow()
    {
        if (_closed || !_window.IsVisible)
        {
            return;
        }

        _presence.JumpTo(0);
        Finish();
    }

    private void MoveTo(double target, TimeSpan duration)
    {
        if (_presence.Target == target && (_presence.IsRunning || _presence.Value == target))
        {
            return;
        }

        if (!_animationsEnabled())
        {
            _presence.JumpTo(target);
            Finish();
            return;
        }

        _presence.Start(target, duration);
        _lastFrame = null;
        _frames.Start();
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        // The first frame anchors the clock, so a slow first render does not eat into the animation.
        var elapsed = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        _presence.Advance(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > MaxFrameStep ? MaxFrameStep : elapsed);
        if (_presence.IsRunning)
        {
            Apply();
        }
        else
        {
            Finish();
        }
    }

    private void Finish()
    {
        _frames.Stop();
        if (_presence.Value == 0 && _window.IsVisible)
        {
            // Hiding resets the surface to shown (OnIsVisibleChanged).
            _window.Hide();
        }
        else
        {
            Apply();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            // However the window was hidden, rest fully shown.
            _frames.Stop();
            _presence.JumpTo(1);
            Apply();
        }
    }

    private void Apply()
    {
        var frame = _motion.FrameAt(_presence.Value);
        _surface.Opacity = frame.Opacity;
        _scale.ScaleX = _scale.ScaleY = frame.Scale;
        _offset.Y = frame.OffsetY;
        FrameApplied?.Invoke(this, frame);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _frames.Stop();
        _frames.Frame -= OnFrame;
        _window.IsVisibleChanged -= OnIsVisibleChanged;
        _window.Closed -= OnClosed;
    }
}
