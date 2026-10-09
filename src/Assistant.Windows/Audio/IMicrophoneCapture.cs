namespace Assistant.Windows.Audio;

/// <summary>Receives one block of captured samples. The samples are only valid during the call.</summary>
internal delegate void MicrophoneSamplesHandler(ReadOnlySpan<short> samples);

/// <summary>Streams the default microphone as 16 kHz mono 16-bit samples.</summary>
internal interface IMicrophoneCapture
{
    /// <summary>
    /// Opens the default microphone and passes every block of samples to <paramref name="onSamples"/> until
    /// <paramref name="stop"/> is signaled, then closes it. Runs on the calling thread.
    /// </summary>
    /// <exception cref="MicrophoneException">The microphone could not be opened, or stopped working.</exception>
    void Run(MicrophoneSamplesHandler onSamples, WaitHandle stop);
}

/// <summary>A microphone failure with the HRESULT that caused it. Its message holds no audio or device details.</summary>
internal sealed class MicrophoneException(MicrophoneFailure failure, int hresult)
    : Exception($"The microphone could not be used ({failure}).")
{
    public MicrophoneFailure Failure { get; } = failure;

    /// <summary>The Windows error that caused the failure.</summary>
    public int ErrorCode { get; } = hresult;
}
