using Assistant.Core.Voice;
using Assistant.Windows.Audio;

namespace Assistant.UI.Voice;

/// <summary>
/// The wake word was heard, and what the microphone heard around it is being kept for the request that follows (PROJECT_SPEC §4.2, step 125): "Kiri, what's on
/// my calendar tomorrow?" is one breath, and the request has begun before the listener has decided that it heard the word. The seed is the audio just before
/// the moment of decision, in memory only; whoever starts listening for the request claims this hand-off, and the listener starts with the seed and then goes on
/// with the live audio, so that no word is lost between them.
/// </summary>
public sealed class WakeHandoff
{
    private readonly List<short[]> _after = [];
    private long _afterSamples;

    internal WakeHandoff(short[] seed) => Seed = seed;

    /// <summary>The audio from a moment before the wake word to the moment it was recognized, 16 kHz mono 16-bit.</summary>
    internal short[] Seed { get; }

    /// <summary>How long the seed lasts.</summary>
    public TimeSpan SeedDuration => VoiceAudio.DurationOf(Seed.Length);

    /// <summary>How much audio has come since the seed, which the request is not to lose.</summary>
    internal TimeSpan Elapsed => VoiceAudio.DurationOf(Interlocked.Read(ref _afterSamples));

    internal void Add(short[] samples)
    {
        lock (_after)
        {
            _after.Add(samples);
            _afterSamples += samples.Length;
        }
    }

    internal short[][] TakeAfter()
    {
        lock (_after)
        {
            var taken = _after.ToArray();
            _after.Clear();
            return taken;
        }
    }
}

/// <summary>How a request is listened to.</summary>
public sealed record VoiceListenRequest
{
    /// <summary>Set when the request follows the wake word: the audio around the word comes first, and the word is taken off the front of what was said.</summary>
    public WakeHandoff? Handoff { get; init; }

    /// <summary>How long a pause after the last word ends the request.</summary>
    public TimeSpan TrailingSilence { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>How long to wait for the first words.</summary>
    public TimeSpan InitialSilence { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>One request being listened to: the microphone is open for it and its words are coming.</summary>
public interface IVoiceListening : IDisposable
{
    /// <summary>How loud the microphone is now, from 0 to 1, for the visualizers.</summary>
    double Level { get; }

    /// <summary>The words so far, and the last of them when the request ends. Raised on a background thread.</summary>
    event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

    /// <summary>The speaker says they are done: what was heard is recognized and the request ends (<see cref="SpeechEndReason.Finished"/>).</summary>
    void Finish();
}

/// <summary>Where voice input gets its microphone and its words (PROJECT_SPEC §4.2, step 125).</summary>
public interface IVoiceInput
{
    bool IsEnabled => true;

    /// <summary>
    /// While <see cref="IsEnabled"/> is false: what to tell the user when the reason is not that they turned voice control off (game mode has it
    /// paused), or <see langword="null"/> when it is.
    /// </summary>
    string? UnavailableMessage => null;

    /// <summary>
    /// Opens the microphone and starts recognizing a request. <paramref name="failed"/> is called once, on a background thread, when the microphone
    /// cannot be used. The microphone stays open until the returned object is disposed.
    /// </summary>
    IVoiceListening Listen(VoiceListenRequest request, Action<MicrophoneFailure> failed);

    /// <summary>Where speech recognition stands: why a request that heard nothing could not be recognized, for example.</summary>
    VoiceEngineStatus RecognizerStatus { get; }
}
