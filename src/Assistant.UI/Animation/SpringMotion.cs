namespace Assistant.UI.Animation;

/// <summary>
/// A damped spring, as the reference's surfaces move: from 0 toward 1 it goes past 1 once, a little, and settles back. It is described by what can
/// be measured on a recording: how much it is damped (<see cref="DampingRatio"/>, below 1 for the overshoot) and when it reaches its farthest
/// (<see cref="PeakTime"/>). A theme token (Themes/Tokens/Motion.xaml).
/// </summary>
public sealed record SpringMotion
{
    // How close to the end the spring must be, and stay, to count as settled.
    private const double SettledWithin = 0.002;

    /// <summary>How much the spring is damped, between 0 and 1: the lower, the farther it overshoots.</summary>
    public double DampingRatio { get; init; } = 0.73;

    /// <summary>How long it takes to reach its farthest point past the end, from the start.</summary>
    public TimeSpan PeakTime { get; init; } = TimeSpan.FromMilliseconds(370);

    /// <summary>How far past the end it goes at its peak, as a share of the way.</summary>
    public double Overshoot => Math.Exp(-Math.PI * Zeta / Math.Sqrt(1 - (Zeta * Zeta)));

    /// <summary>How long it takes to settle at the end.</summary>
    public TimeSpan SettleTime =>
        TimeSpan.FromSeconds(Math.Log(1 / (SettledWithin * Math.Sqrt(1 - (Zeta * Zeta)))) / (Zeta * NaturalFrequency));

    /// <summary>How far the spring has come from 0 toward 1, <paramref name="elapsed"/> after it was let go: past 1 around <see cref="PeakTime"/>.</summary>
    public double ValueAt(TimeSpan elapsed)
    {
        var t = elapsed.TotalSeconds;
        if (t <= 0)
        {
            return 0;
        }

        if (elapsed >= SettleTime)
        {
            return 1;
        }

        var damped = DampedFrequency;
        var decay = Math.Exp(-Zeta * NaturalFrequency * t);
        return 1 - (decay * (Math.Cos(damped * t) + (Zeta * NaturalFrequency / damped * Math.Sin(damped * t))));
    }

    private double Zeta => Math.Clamp(DampingRatio, 0.05, 0.95);

    private double DampedFrequency => Math.PI / Math.Max(0.01, PeakTime.TotalSeconds);

    private double NaturalFrequency => DampedFrequency / Math.Sqrt(1 - (Zeta * Zeta));
}

/// <summary>
/// Moves a value from 0 to 1 along a <see cref="SpringMotion"/>, after a wait, one frame at a time. It keeps no clock of its own: the caller advances it.
/// </summary>
internal sealed class SpringTween(SpringMotion motion, TimeSpan delay)
{
    private TimeSpan _elapsed;

    /// <summary>How far it has come; past 1 while it overshoots.</summary>
    public double Value { get; private set; }

    /// <summary>Whether it is still waiting or moving.</summary>
    public bool IsRunning { get; private set; } = true;

    /// <summary>Moves on by <paramref name="elapsed"/>, resting exactly at 1 once it has settled.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (!IsRunning)
        {
            return;
        }

        _elapsed += elapsed;
        var moving = _elapsed - delay;
        if (moving >= motion.SettleTime)
        {
            Value = 1;
            IsRunning = false;
            return;
        }

        Value = motion.ValueAt(moving);
    }
}
