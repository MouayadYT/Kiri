using Assistant.UI.Voice;

namespace Assistant.UI.Orb;

/// <summary>
/// Where the assistant orb reads how loud the sound is, once per frame while it is listening. This is the one input the
/// voice pipeline feeds the orb through, so a real microphone replaces the demo source without changing the orb.
/// </summary>
public interface IOrbAmplitudeSource
{
    /// <summary>
    /// The sound's current loudness, normalized from 0 (silence) to 1 (as loud as speech gets). Reading it has no side
    /// effects, so several orbs may share one source.
    /// </summary>
    double ReadAmplitude();
}

/// <summary>
/// Feeds the orb from a microphone level source: it turns the root-mean-square level the microphone reports into the
/// normalized amplitude the orb reads, on the same decibel scale the voice glow uses.
/// </summary>
public sealed class VoiceLevelAmplitude : IOrbAmplitudeSource
{
    private readonly IVoiceLevelSource _levels;

    public VoiceLevelAmplitude(IVoiceLevelSource levels) => _levels = levels;

    /// <inheritdoc/>
    public double ReadAmplitude() => VoiceEnergy.Loudness(_levels.ReadLevel());
}
