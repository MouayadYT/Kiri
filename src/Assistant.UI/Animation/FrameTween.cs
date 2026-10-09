namespace Assistant.UI.Animation;

/// <summary>
/// A value between 0 and 1 that eases toward a target one rendered frame at a time, for a part of a surface that comes and goes on its own (the
/// answer to a sum in the bar, the list of results), and that can be turned around part way without jumping. With Windows animation effects off
/// (PROJECT_SPEC §4.0) it moves at once. Whoever owns it draws each <see cref="Value"/> it announces.
/// </summary>
internal sealed class FrameTween : IDisposable
{
    // A frame later than this counts as this long, so a stalled frame slows the tween rather than skipping it.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly PresenceTween _tween;
    private TimeSpan? _lastFrame;

    public FrameTween(IFrameSource frames, Func<bool> animationsEnabled, double value = 0, Func<double, double>? ease = null)
    {
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        _tween = new PresenceTween(value, ease);
        frames.Frame += OnFrame;
    }

    /// <summary>Raised whenever <see cref="Value"/> has changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The value now.</summary>
    public double Value => _tween.Value;

    /// <summary>The value it is heading for, or rests at.</summary>
    public double Target => _tween.Target;

    /// <summary>Whether it is on its way.</summary>
    public bool IsRunning => _tween.IsRunning;

    /// <summary>Eases to <paramref name="target"/> from wherever it is; <paramref name="fullDuration"/> is the time for the whole way.</summary>
    public void MoveTo(double target, TimeSpan fullDuration)
    {
        if (_tween.Target == target && (_tween.IsRunning || _tween.Value == target))
        {
            return;
        }

        if (!_animationsEnabled())
        {
            JumpTo(target);
            return;
        }

        _tween.Start(target, fullDuration);
        _lastFrame = null;
        if (_tween.IsRunning)
        {
            _frames.Start();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rests at <paramref name="value"/> at once.</summary>
    public void JumpTo(double value)
    {
        _frames.Stop();
        var changed = _tween.Value != value || _tween.IsRunning;
        _tween.JumpTo(value);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops the frame loop and stops listening to it, for good.</summary>
    public void Dispose()
    {
        _frames.Stop();
        _frames.Frame -= OnFrame;
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        var elapsed = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        _tween.Advance(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > MaxFrameStep ? MaxFrameStep : elapsed);
        if (!_tween.IsRunning)
        {
            _frames.Stop();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
