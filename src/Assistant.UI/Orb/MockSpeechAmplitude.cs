using System.Diagnostics;

namespace Assistant.UI.Orb;

/// <summary>
/// Invents the loudness of someone speaking, so the orb can be watched and tested without a microphone: phrases of
/// quick syllables at whispering, ordinary and raised loudness, each syllable at its own strength, with pauses of
/// silence between. It reads no audio and records nothing. It is a demo source only: the voice pipeline replaces it
/// with the microphone's level (<see cref="VoiceLevelAmplitude"/>).
/// </summary>
public sealed class MockSpeechAmplitude : IOrbAmplitudeSource
{
    // How loud the phrases are, by kind: a whisper, ordinary speech, raised speech and a shout.
    private static readonly double[] Loudness = [0.16, 0.5, 0.5, 0.82, 0.98];

    private readonly Func<TimeSpan> _clock;
    private readonly Random _random;
    private readonly List<Syllable> _syllables = [];
    private double _plannedTo;
    private double _latest;

    public MockSpeechAmplitude(int seed = 7) : this(StartClock(), seed)
    {
    }

    /// <summary>Speaks against <paramref name="clock"/>, the time since the source began.</summary>
    public MockSpeechAmplitude(Func<TimeSpan> clock, int seed)
    {
        _clock = clock;
        _random = new Random(seed);
    }

    /// <summary>How loud the invented speech is now, from 0 to 1.</summary>
    public double ReadAmplitude() => At(_clock().TotalSeconds);

    /// <summary>How loud the invented speech is at <paramref name="seconds"/> after it began.</summary>
    public double At(double seconds)
    {
        _latest = Math.Max(_latest, seconds);
        Plan(seconds + 4);
        var sum = 0.0;
        foreach (var syllable in _syllables)
        {
            var t = (seconds - syllable.Start) / syllable.Length;
            if (t is > 0 and < 1)
            {
                // A raised cosine: the syllable rises and falls smoothly.
                sum = Math.Max(sum, syllable.Peak * 0.5 * (1 - Math.Cos(Math.Tau * t)));
            }
        }

        // What can no longer matter is forgotten, so the list does not grow.
        _syllables.RemoveAll(syllable => syllable.Start + syllable.Length < _latest - 30);
        return Math.Clamp(sum, 0, 1);
    }

    private static Func<TimeSpan> StartClock()
    {
        var watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

    // Plans phrases and the pauses between them as far ahead as needed.
    private void Plan(double to)
    {
        while (_plannedTo < to)
        {
            // A pause of silence, then a phrase.
            _plannedTo += 0.7 + (_random.NextDouble() * 0.9);
            var phrase = _plannedTo + 1.4 + (_random.NextDouble() * 1.8);
            var loudness = Loudness[_random.Next(Loudness.Length)];
            while (_plannedTo < phrase)
            {
                var length = 0.14 + (_random.NextDouble() * 0.10);
                _syllables.Add(new Syllable(_plannedTo, length, loudness * (0.55 + (0.45 * _random.NextDouble()))));

                // Syllables come quick and unevenly, sometimes running into each other.
                _plannedTo += length * (0.75 + (_random.NextDouble() * 0.9));
            }

            // The phrase trails off.
            _syllables.Add(new Syllable(_plannedTo, 0.32, loudness * 0.4));
            _plannedTo += 0.32;
        }
    }

    private readonly record struct Syllable(double Start, double Length, double Peak);
}
