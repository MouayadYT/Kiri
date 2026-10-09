namespace Assistant.UI.Voice;

/// <summary>Where a voice visualizer reads how loud the microphone is.</summary>
public interface IVoiceLevelSource
{
    /// <summary>
    /// The microphone's current loudness, as root-mean-square amplitude from 0 (silence) to 1 (full scale), or 0 while
    /// it is not listening. Reading it has no side effects, so several visualizers may share one source.
    /// </summary>
    double ReadLevel();
}
