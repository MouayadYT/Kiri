namespace Assistant.Core.Settings;

/// <summary>
/// Spoken answers and the wake word (PROJECT_SPEC §4.2 Speech, §5.10). The voice runtime follows these settings: the
/// engine that speaks answers is applied as soon as it is chosen, and the wake word listener runs only while it is on.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>Shared Handy, installed Windows recognizer, or a managed local ASR model.</summary>
    public string SpeechRecognitionModelId { get; init; } = "speech-recognition";
    /// <summary>CPU or the bundled Vulkan GPU backend. Handy owns its device selection.</summary>
    public string SpeechRecognitionDevice { get; init; } = "cpu";
    /// <summary>The word that wakes the Assistant, listened for on this PC alone.</summary>
    public const string WakeWord = "Kiri";

    /// <summary>
    /// Identifier of the text-to-speech model (<see cref="TextToSpeechModels"/>) that speaks answers. An
    /// identifier no model has is replaced by the default when the settings are loaded.
    /// </summary>
    public string TextToSpeechModelId { get; init; } = TextToSpeechModels.DefaultId;

    /// <summary>
    /// Whether the Assistant listens, only on this PC and keeping nothing, for <see cref="WakeWord"/> to start voice input
    /// without a key press. Off by default: listening all the time is something the user must choose.
    /// </summary>
    public bool WakeWordEnabled { get; init; }
    /// <summary>Whether microphone input is enabled. Existing installs keep manual input until setup changes it.</summary>
    public bool VoiceInputEnabled { get; init; } = true;

    /// <summary>The microphone to listen to: the id Windows gave it, or <see langword="null"/> for the one Windows uses. A device that is not there is not an error: the default is used.</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>OpenAI-compatible speech endpoint. The API key is kept in the secret store.</summary>
    public string SpeechApiEndpoint { get; init; } = "https://api.openai.com/v1/audio/speech";
    public string SpeechApiModel { get; init; } = "tts-1";
    public string SpeechApiVoice { get; init; } = "alloy";

    /// <summary>CPU or a stable NVIDIA UUID prefixed with cuda:.</summary>
    public string TextToSpeechDevice { get; init; } = "cpu";
}
