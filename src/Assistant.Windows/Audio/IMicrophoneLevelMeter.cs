namespace Assistant.Windows.Audio;

/// <summary>
/// Measures how loud the default microphone is, for voice-input feedback. It never records, stores, transcribes or
/// logs audio: each block of samples is reduced to one level and discarded.
/// </summary>
public interface IMicrophoneLevelMeter
{
    /// <summary>
    /// Opens the default recording device and starts measuring it. The microphone stays open until the returned
    /// session is disposed. The device opens in the background, so this returns at once.
    /// </summary>
    /// <param name="failed">
    /// Called at most once, on a background thread, if the microphone cannot be opened or stops working before the
    /// session is disposed.
    /// </param>
    IMicrophoneLevelSession Start(Action<MicrophoneFailure> failed);
}

/// <summary>An open microphone being measured. Dispose it to close the microphone.</summary>
public interface IMicrophoneLevelSession : IDisposable
{
    /// <summary>
    /// Loudness of the most recent audio, as its root-mean-square amplitude from 0 (silence) to 1 (full scale). It is
    /// 0 until audio arrives and after the microphone closes. It can be read from any thread.
    /// </summary>
    double Level { get; }
}

/// <summary>Why the microphone could not be used.</summary>
public enum MicrophoneFailure
{
    /// <summary>There is no recording device.</summary>
    NoMicrophone,

    /// <summary>Windows privacy settings do not let desktop apps use the microphone.</summary>
    AccessDenied,

    /// <summary>The recording device was removed or disabled while in use.</summary>
    Disconnected,

    /// <summary>The recording device could not be used for another reason, such as another app holding it exclusively.</summary>
    Unavailable,
}
