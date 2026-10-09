namespace Assistant.UI.Animation;

/// <summary>
/// Drives the growth of the Assistant's surface between its forms, one rendered frame at a time, as a progress from 0 to 1 that its owner draws
/// between two forms: eased (the pill growing into the panel, which can be turned around part way and never jumps), or along a spring that goes
/// a little past 1 and settles (the bar folding into the Working pill, and the pill opening into the panel). With Windows animation effects off
/// (PROJECT_SPEC §4.0) it arrives at once. It knows nothing of windows: whoever owns the surface draws each <see cref="Progress"/> it announces.
/// </summary>
internal sealed class SurfaceMorph : IDisposable
{
    // A frame later than this counts as this long, so a stalled frame slows the growth rather than skipping it.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private readonly SurfaceMorphMotion _motion;
    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly PresenceTween _progress = new(0);
    private SpringTween? _spring;
    private TimeSpan? _lastFrame;

    public SurfaceMorph(SurfaceMorphMotion motion, IFrameSource frames, Func<bool> animationsEnabled)
    {
        _motion = motion;
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        frames.Frame += OnFrame;
    }

    /// <summary>Raised whenever <see cref="Progress"/> has changed, so the surface can be drawn at it.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the surface arrives at the form it was heading for, by animating or at once.</summary>
    public event EventHandler? Arrived;

    /// <summary>How far the surface has come from one form (0) to the other (1), after easing; a spring takes it a little past 1 on the way.</summary>
    public double Progress => _spring?.Value ?? _progress.Value;

    /// <summary>Whether the surface is on its way between forms.</summary>
    public bool IsRunning => _spring?.IsRunning ?? _progress.IsRunning;

    /// <summary>Grows toward <paramref name="target"/> from wherever the surface is, eased, in the motion's time.</summary>
    public void GrowTo(double target)
    {
        if (_spring is null && _progress.Target == target && (_progress.IsRunning || _progress.Value == target))
        {
            return;
        }

        if (_spring is { } spring)
        {
            // From wherever the spring had got to, within the eased range.
            _spring = null;
            _progress.JumpTo(spring.Value);
        }

        if (!_animationsEnabled())
        {
            Rest(target);
            return;
        }

        _progress.Start(target, _motion.Duration);
        _lastFrame = null;
        if (_progress.IsRunning)
        {
            _frames.Start();
        }
        else
        {
            Arrive();
        }
    }

    /// <summary>Starts again from 0 and eases to 1 in <paramref name="duration"/>.</summary>
    public void EaseFromStart(TimeSpan duration)
    {
        _spring = null;
        _progress.JumpTo(0);
        if (!_animationsEnabled() || duration <= TimeSpan.Zero)
        {
            Rest(1);
            return;
        }

        _progress.Start(1, duration);
        _lastFrame = null;
        Changed?.Invoke(this, EventArgs.Empty);
        _frames.Start();
    }

    /// <summary>Starts again from 0 and springs to 1 along <paramref name="spring"/>, after <paramref name="delay"/> held at 0.</summary>
    public void SpringFromStart(SpringMotion spring, TimeSpan delay = default)
    {
        _progress.JumpTo(0);
        if (!_animationsEnabled())
        {
            _spring = null;
            Rest(1);
            return;
        }

        _spring = new SpringTween(spring, delay);
        _lastFrame = null;
        Changed?.Invoke(this, EventArgs.Empty);
        _frames.Start();
    }

    /// <summary>Stops wherever it is and rests at <paramref name="progress"/>, drawing it, without announcing an arrival.</summary>
    public void JumpTo(double progress)
    {
        _frames.Stop();
        _spring = null;
        _progress.JumpTo(progress);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the frame loop and stops listening to it, for good.</summary>
    public void Dispose()
    {
        _frames.Stop();
        _frames.Frame -= OnFrame;
    }

    private void Rest(double target)
    {
        JumpTo(target);
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        // The first frame anchors the clock, so a slow first render does not eat into the growth.
        var elapsed = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        var step = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > MaxFrameStep ? MaxFrameStep : elapsed;
        if (_spring is { } spring)
        {
            spring.Advance(step);
        }
        else
        {
            _progress.Advance(step);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (!IsRunning)
        {
            Arrive();
        }
    }

    private void Arrive()
    {
        _frames.Stop();
        Arrived?.Invoke(this, EventArgs.Empty);
    }
}
