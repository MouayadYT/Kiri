namespace Assistant.Core.Voice;

/// <summary>The speakers: opens a stream of sound to play, which a text-to-speech engine fills as it speaks. Implemented by the platform (WASAPI on Windows).</summary>
public interface IAudioOutput
{
    /// <summary>
    /// Opens a stream that plays mono samples at <paramref name="sampleRate"/> hertz on the default speakers. Nothing sounds until the first
    /// <see cref="IAudioOutputStream.Write"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is nothing to play on.</exception>
    IAudioOutputStream Open(int sampleRate);
}

/// <summary>A stream of speech on the speakers. The queue is filled from one thread and played by the device's own.</summary>
public interface IAudioOutputStream : IDisposable
{
    /// <summary>
    /// Adds <paramref name="samples"/> (mono, from -1 to 1) to the end of what is queued and returns at once; the samples are copied. The first call after
    /// the queue was empty starts the sound.
    /// </summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>Drops everything that is queued and silences the speakers at once, within one device buffer.</summary>
    void Clear();

    /// <summary>How much sound is queued and not played yet.</summary>
    TimeSpan Queued { get; }

    /// <summary>Completes once everything queued so far has been played. A later write starts it again.</summary>
    Task WaitUntilDrainedAsync(CancellationToken cancellationToken);
}
