using Assistant.Core.Settings;

namespace Assistant.Core.Voice;

/// <summary>Receives a block of speech as soon as an engine has made it. Return <see langword="false"/> to make the engine stop.</summary>
/// <param name="samples">Mono samples from -1 to 1 at the engine's <see cref="ITextToSpeechEngine.SampleRate"/>. They are only valid during the call.</param>
public delegate bool SpeechAudioHandler(ReadOnlySpan<float> samples);

/// <summary>
/// One text-to-speech engine behind its adapter (PROJECT_SPEC §4.2, step 125): KittenTTS Mini, Kokoro-82M or Piper, each run on this PC from its own files
/// by whatever runtime suits it. Nothing but this contract and <see cref="ITextToSpeechEngineFactory"/> knows which engine it is, so the conversation, the
/// window and the Settings page never depend on one. An engine is not thread-safe: <see cref="TextToSpeechService"/> uses one at a time.
/// </summary>
public interface ITextToSpeechEngine : IDisposable
{
    /// <summary>Which engine this is.</summary>
    TextToSpeechModel Model { get; }

    /// <summary>The rate of the samples it makes, in hertz. Known once <see cref="Load"/> has returned.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Reads the engine's files into memory and makes a first, silent sentence with them so that the first real one is as quick as the rest.
    /// Blocking: call it off the UI thread.
    /// </summary>
    /// <exception cref="VoiceEngineException">The engine's files are missing, damaged or cannot be loaded.</exception>
    void Load();

    /// <summary>
    /// Makes speech of <paramref name="text"/> and passes it to <paramref name="onAudio"/> block by block as soon as each is ready, so playback can start
    /// before the sentence is finished. Blocking: call it off the UI thread, one text at a time.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, or <paramref name="onAudio"/> asked to stop.</exception>
    /// <exception cref="VoiceEngineException">The engine failed.</exception>
    void Synthesize(string text, SpeechAudioHandler onAudio, CancellationToken cancellationToken);
}

/// <summary>Makes the engine adapter for a model. The only place that knows which runtime runs which engine, and where its files are.</summary>
public interface ITextToSpeechEngineFactory
{
    /// <summary>
    /// Makes the adapter for <paramref name="model"/>. Nothing is read: <see cref="ITextToSpeechEngine.Load"/> does that.
    /// </summary>
    ITextToSpeechEngine Create(TextToSpeechModel model);
}
