namespace Assistant.UI.Voice;

/// <summary>
/// Turns microphone levels into the energy a voice visualizer shows, from 0 to 1. Quiet rooms read as 0, speech rises
/// quickly, and the energy settles back smoothly when the speech stops. It keeps no clock of its own: the caller
/// updates it frame by frame.
/// </summary>
internal sealed class VoiceEnergy
{
    /// <summary>Loudness, in dB below full scale, at or under which nothing shows: room and microphone noise.</summary>
    public const double QuietDb = -55;

    /// <summary>Loudness, in dB below full scale, at or over which the energy is full: close, raised speech.</summary>
    public const double LoudDb = -20;

    // How quickly the energy follows louder and quieter sound.
    private static readonly TimeSpan Rise = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(380);

    // Below this the energy is treated as settled.
    private const double Rest = 0.002;

    /// <summary>The current energy, from 0 (at rest) to 1.</summary>
    public double Value { get; private set; }

    /// <summary>Loudness on a 0 to 1 scale between <see cref="QuietDb"/> and <see cref="LoudDb"/>, even in decibels.</summary>
    public static double Loudness(double level)
    {
        if (!(level > 0))
        {
            return 0;
        }

        var decibels = 20 * Math.Log10(Math.Min(level, 1));
        return Math.Clamp((decibels - QuietDb) / (LoudDb - QuietDb), 0, 1);
    }

    /// <summary>Moves toward the loudness of <paramref name="level"/> over <paramref name="elapsed"/>.</summary>
    public void Update(double level, TimeSpan elapsed)
    {
        var target = Loudness(level);
        var response = target > Value ? Rise : Settle;
        Value += (target - Value) * (1 - Math.Exp(-elapsed / response));
        if (target == 0 && Value < Rest)
        {
            Value = 0;
        }
    }

    /// <summary>Returns to rest at once.</summary>
    public void Reset() => Value = 0;
}
