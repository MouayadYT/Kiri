namespace Assistant.Voice;

/// <summary>The level of the quietest audio lately, which rises slowly and falls quickly: what a silent room sounds like.</summary>
internal sealed class NoiseFloor
{
    private double _floor = 0.0015;

    /// <summary>Takes in the level of the next block of audio and returns the noise floor now.</summary>
    public double Update(double level)
    {
        // Quicker to believe a quieter room than a louder one, so speech does not raise the floor.
        _floor = level < _floor ? _floor + ((level - _floor) * 0.2) : _floor + ((level - _floor) * 0.0015);
        return Math.Clamp(_floor, 0.0003, 0.05);
    }
}

/// <summary>
/// Decides that a speaker has finished a request (PROJECT_SPEC §4.2, step 125). The streaming recognizer writes the words as they are said but does not
/// reliably say when the speaker stopped, so the decision is made here from what is heard: the request is over when the audio has been quiet for a moment
/// <i>and</i> no new word has come out of the recognizer for the pause the request allows. Neither alone will do: a quiet room is told by its level, but a
/// breath between two sentences is quiet too, and the words alone arrive in bursts, a little behind the speaker. A noisy room, in which the audio is never
/// quiet, ends the request on the words alone, a little later.
/// </summary>
internal sealed class EndOfSpeechDetector
{
    // How much louder than the room's noise audio must be to count as sound, and the least level that is ever sound.
    private const double FloorFactor = 3.5;
    private const double LeastLevel = 0.006;

    // The extra wait, past the pause, when the audio is never quiet.
    private static readonly TimeSpan NoisyRoomExtra = TimeSpan.FromSeconds(1.5);

    private readonly NoiseFloor _floor = new();
    private readonly double _pause;
    private double _quietFor;

    /// <summary>Creates a detector that ends the request after a pause of <paramref name="pause"/>.</summary>
    public EndOfSpeechDetector(TimeSpan pause) => _pause = pause.TotalSeconds;

    /// <summary>How long the audio has been quiet, in seconds.</summary>
    public double QuietFor => _quietFor;

    /// <summary>
    /// Takes in the next block of audio.
    /// </summary>
    /// <param name="level">The block's level, from 0 to 1.</param>
    /// <param name="blockSeconds">How long the block lasts.</param>
    /// <param name="audioSeconds">How much audio has been heard in all, up to the end of this block.</param>
    /// <param name="lastWordSeconds">When the last word the recognizer has written was said, in the same time, or <see langword="null"/> when it has written none.</param>
    /// <returns>Whether the request is over.</returns>
    public bool Update(double level, double blockSeconds, double audioSeconds, double? lastWordSeconds)
    {
        var floor = _floor.Update(level);
        _quietFor = level < Math.Max(LeastLevel, floor * FloorFactor) ? _quietFor + blockSeconds : 0;
        if (lastWordSeconds is not { } lastWord)
        {
            return false;
        }

        var sinceLastWord = audioSeconds - lastWord;
        return (_quietFor >= Math.Max(0.6, _pause * 0.8) && sinceLastWord >= _pause) || sinceLastWord >= _pause + NoisyRoomExtra.TotalSeconds;
    }
}
