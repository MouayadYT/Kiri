namespace Assistant.UI.Animation;

/// <summary>
/// Moves a presence value between 0 (hidden) and 1 (shown) over time, easing out toward whichever end it is heading
/// for. Turned around part way, it heads back from where it is, so the value never jumps. It keeps no clock of its
/// own: the caller advances it frame by frame.
/// </summary>
internal sealed class PresenceTween
{
    private readonly Func<double, double> _ease;
    private double _from;
    private TimeSpan _duration;
    private TimeSpan _elapsed;

    /// <summary>
    /// Rests at <paramref name="value"/>. <paramref name="ease"/> turns the share of the time that has passed into the share of the way that is
    /// covered; left out, it is <see cref="EaseOut"/>.
    /// </summary>
    public PresenceTween(double value, Func<double, double>? ease = null)
    {
        _ease = ease ?? EaseOut;
        JumpTo(value);
    }

    /// <summary>The current presence.</summary>
    public double Value { get; private set; }

    /// <summary>The presence it is heading for, or rests at.</summary>
    public double Target { get; private set; }

    /// <summary>Whether it is still on its way to <see cref="Target"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Stops wherever it is heading and rests at <paramref name="value"/>.</summary>
    public void JumpTo(double value)
    {
        Value = Target = Math.Clamp(value, 0, 1);
        IsRunning = false;
    }

    /// <summary>
    /// Heads for <paramref name="target"/> from the current value. <paramref name="fullDuration"/> is the time for the
    /// whole way from the opposite end; a shorter way takes the matching share of it.
    /// </summary>
    public void Start(double target, TimeSpan fullDuration)
    {
        Target = Math.Clamp(target, 0, 1);
        _from = Value;
        _elapsed = TimeSpan.Zero;
        _duration = fullDuration * Math.Abs(Target - Value);
        IsRunning = _duration > TimeSpan.Zero;
        if (!IsRunning)
        {
            Value = Target;
        }
    }

    /// <summary>Moves on by <paramref name="elapsed"/>, arriving exactly at <see cref="Target"/> when time is up.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (!IsRunning)
        {
            return;
        }

        _elapsed += elapsed;
        var progress = Math.Min(1, _elapsed / _duration);
        Value = progress < 1 ? _from + ((Target - _from) * _ease(progress)) : Target;
        IsRunning = progress < 1;
    }

    /// <summary>Cubic ease-out: fast at first, settling gently.</summary>
    public static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);

    /// <summary>Quadratic ease-out: slowing evenly all the way, less sudden at first than <see cref="EaseOut"/>.</summary>
    public static double EaseOutGently(double progress) => 1 - ((1 - progress) * (1 - progress));

    /// <summary>No easing: as far along as the time is, for whoever shapes the way itself.</summary>
    public static double Evenly(double progress) => progress;
}
