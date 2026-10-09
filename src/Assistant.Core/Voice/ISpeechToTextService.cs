namespace Assistant.Core.Voice;

/// <summary>The audio that goes to speech recognition and the wake-word listener: what the microphone is read as.</summary>
public static class VoiceAudio
{
    /// <summary>Samples per second of the audio the recognizers take: 16 kHz, mono, 16-bit.</summary>
    public const int SampleRate = 16_000;

    /// <summary>How many samples there are in <paramref name="duration"/> of audio.</summary>
    public static int SamplesIn(TimeSpan duration) => (int)(duration.TotalSeconds * SampleRate);

    /// <summary>How long <paramref name="samples"/> samples last.</summary>
    public static TimeSpan DurationOf(long samples) => TimeSpan.FromSeconds(samples / (double)SampleRate);
}

/// <summary>Why a recognition session ended.</summary>
public enum SpeechEndReason
{
    /// <summary>The speaker stopped: the recognizer heard the end of a sentence.</summary>
    Endpoint,

    /// <summary>Nothing was said for as long as the session waits for something to be.</summary>
    NoSpeech,

    /// <summary>The audio was ended by <see cref="ISpeechRecognitionSession.Finish"/>.</summary>
    Finished,

    /// <summary>The speaker went on for as long as one request may.</summary>
    TooLong,
}

/// <summary>What a recognition session has heard.</summary>
public sealed class SpeechTranscriptEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public SpeechTranscriptEventArgs(string text, SpeechEndReason? ended)
    {
        Text = text ?? "";
        Ended = ended;
    }

    /// <summary>The words so far, or all of them when <see cref="Ended"/> is set. May be empty. Private: never logged.</summary>
    public string Text { get; }

    /// <summary>Why the session has ended, when it has: <see langword="null"/> for a change in the words of a session that goes on.</summary>
    public SpeechEndReason? Ended { get; }

    /// <summary>Whether this is the session's last word.</summary>
    public bool IsFinal => Ended is not null;
}

/// <summary>How a recognition session listens.</summary>
public sealed record SpeechRecognitionOptions
{
    /// <summary>How long the session waits for the first words before it gives up (<see cref="SpeechEndReason.NoSpeech"/>).</summary>
    public TimeSpan InitialSilence { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long a pause after speech ends the request (<see cref="SpeechEndReason.Endpoint"/>).</summary>
    public TimeSpan TrailingSilence { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>The longest one request may be (<see cref="SpeechEndReason.TooLong"/>).</summary>
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Words that begin before this far into the audio are left out of the text: the wake word and what came just before it, when the session was
    /// started with the audio that was heard around it.
    /// </summary>
    public TimeSpan IgnoreWordsBefore { get; init; } = TimeSpan.Zero;
}

/// <summary>The result of measuring a recognizer with a short local audio sample.</summary>
public sealed record SpeechToTextBenchmarkResult(
    string ModelId,
    TimeSpan? LoadTime,
    TimeSpan? RecognitionTime,
    TimeSpan AudioDuration,
    string Transcript,
    string? Failure = null)
{
    /// <summary>Whether the recognizer produced a usable timing measurement.</summary>
    public bool HasSamples => Failure is null && RecognitionTime is not null;

    /// <summary>Audio seconds processed per recognition second. Values above 1 are faster than real time.</summary>
    public double SpeedMultiple => RecognitionTime is { TotalSeconds: > 0 } time
        ? AudioDuration.TotalSeconds / time.TotalSeconds
        : 0;
}

/// <summary>
/// One request being listened to (PROJECT_SPEC §4.2, step 125): the microphone's audio goes in as it is heard and the words come out as they are
/// recognized, a few at a time, with the last of them when the speaker stops. Everything is on this PC and in memory: the audio and the words are not
/// recorded, kept or logged, and the session forgets them when it is disposed.
/// </summary>
public interface ISpeechRecognitionSession : IDisposable
{
    /// <summary>
    /// Raised, on a thread of the recognizer's, whenever the words so far change, and once more when the session ends (<see cref="SpeechTranscriptEventArgs.Ended"/>).
    /// </summary>
    event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

    /// <summary>
    /// Adds the next audio, 16 kHz mono 16-bit (<see cref="VoiceAudio.SampleRate"/>). It returns at once: the audio is copied and queued, and recognized on the
    /// recognizer's own thread, so the microphone's thread is never held up. Audio after the session ended is ignored.
    /// </summary>
    void Push(ReadOnlySpan<short> samples);

    /// <summary>No more audio is coming: what has been heard is recognized as far as it can be, and the session ends (<see cref="SpeechEndReason.Finished"/>).</summary>
    void Finish();

    /// <summary>Whether the session has ended.</summary>
    bool IsEnded { get; }
}

/// <summary>
/// Turns what the microphone hears into words, on this PC (PROJECT_SPEC §4.2, step 125): a small streaming recognizer that is read into memory when it is first
/// needed and answers while the speaker is still talking. Nothing is sent anywhere, and no speech server is needed.
/// </summary>
public interface ISpeechToTextService : IDisposable
{
    /// <summary>Selects the ASR provider and releases the previous recognizer.</summary>
    Task ConfigureAsync(Assistant.Core.Settings.VoiceSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <summary>Where the recognizer stands.</summary>
    VoiceEngineStatus Status { get; }

    /// <summary>Raised when <see cref="Status"/> changes, on any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Loads the recognizer if it is not loaded, so that the first request does not wait for it. Never throws: <see cref="Status"/> says what happened.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>Measures model loading and recognition locally with the supplied 16 kHz mono PCM sample.</summary>
    Task<SpeechToTextBenchmarkResult> BenchmarkAsync(ReadOnlyMemory<short> audio, CancellationToken cancellationToken = default)
        => Task.FromException<SpeechToTextBenchmarkResult>(new NotSupportedException("This speech recognizer does not provide a speed test."));

    /// <summary>
    /// Starts listening to one request. Audio pushed before the recognizer is loaded is held (a few seconds of it) and recognized as soon as it is.
    /// A recognizer that cannot be loaded ends the session at once (<see cref="SpeechEndReason.Finished"/>, no words).
    /// </summary>
    ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null);

    /// <summary>Lets go of the recognizer's memory. It loads again when it is next needed.</summary>
    void Unload();
}
