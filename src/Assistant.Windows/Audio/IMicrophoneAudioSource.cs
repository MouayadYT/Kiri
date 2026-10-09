namespace Assistant.Windows.Audio;

/// <summary>Receives a block of the microphone's audio: 16 kHz, mono, 16-bit. It runs on the capture thread and must be quick: copy the samples, never keep the span.</summary>
public delegate void MicrophoneAudioHandler(ReadOnlySpan<short> samples);

/// <summary>
/// The default microphone, shared (PROJECT_SPEC §4.2, step 125): the voice glow, speech recognition and the wake-word listener each take what they need from
/// one capture, and the device is open only while at least one of them subscribes. The audio is handed on and never recorded, stored or logged here.
/// </summary>
public interface IMicrophoneAudioSource
{
    /// <summary>
    /// Subscribes to the microphone, opening it in the background if nothing else has it open. The microphone stays open until the last subscription is
    /// disposed.
    /// </summary>
    /// <param name="onSamples">Called with every block of audio, on the capture thread, or <see langword="null"/> to only measure the level.</param>
    /// <param name="failed">Called at most once, on a background thread, if the microphone cannot be opened or stops working before the subscription is disposed.</param>
    IMicrophoneAudioSubscription Subscribe(MicrophoneAudioHandler? onSamples, Action<MicrophoneFailure> failed);
}

/// <summary>One subscription to the microphone. Dispose it to let go of the microphone; the device closes when the last one does.</summary>
public interface IMicrophoneAudioSubscription : IMicrophoneLevelSession
{
}
