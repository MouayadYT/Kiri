namespace Assistant.Core.Voice;

/// <summary>The wake word was heard.</summary>
public sealed class WakeWordDetectedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public WakeWordDetectedEventArgs(TimeSpan heardAt)
    {
        HeardAt = heardAt;
    }

    /// <summary>How far into the audio the session was given the listener decided it had heard the word: a little after the word ended.</summary>
    public TimeSpan HeardAt { get; }
}

/// <summary>
/// A listener for the wake word, "Kiri" (PROJECT_SPEC §4.2, step 125): the microphone's audio goes in and an event comes out when the word is heard. It
/// keeps no audio beyond the second or so it needs to follow a word, nothing is sent anywhere, and nothing it hears is recorded or logged.
/// </summary>
public interface IWakeWordSession : IDisposable
{
    /// <summary>Raised, on a thread of the listener's, when the wake word was heard. Once per word: the listener starts again after it.</summary>
    event EventHandler<WakeWordDetectedEventArgs>? Detected;

    /// <summary>
    /// Adds the next audio, 16 kHz mono 16-bit (<see cref="VoiceAudio.SampleRate"/>). It returns at once: the audio is copied and queued, and listened to on the
    /// listener's own thread. Audio that quiet rooms make is dropped before it is, so a silent room costs next to nothing.
    /// </summary>
    void Push(ReadOnlySpan<short> samples);

    /// <summary>Forgets what was heard so far, so a word spoken before this moment cannot be completed by what comes after it.</summary>
    void Reset();
}

/// <summary>
/// Makes the listener for the wake word, on this PC (PROJECT_SPEC §4.2, step 125): a tiny recognizer that is read into memory when the wake word is turned
/// on, and costs a small part of one processor core while it listens.
/// </summary>
public interface IWakeWordService : IDisposable
{
    /// <summary>Where the listener's model stands.</summary>
    VoiceEngineStatus Status { get; }

    /// <summary>Raised when <see cref="Status"/> changes, on any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Loads the listener's model if it is not loaded. Never throws: <see cref="Status"/> says what happened.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts listening for the word. Audio pushed before the model is loaded is dropped. A model that cannot be loaded makes a session that never hears
    /// anything (<see cref="Status"/> says why).
    /// </summary>
    IWakeWordSession StartSession();

    /// <summary>Lets go of the model's memory.</summary>
    void Unload();
}
