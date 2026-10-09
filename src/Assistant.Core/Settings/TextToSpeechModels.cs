namespace Assistant.Core.Settings;

/// <summary>A text-to-speech model the user can choose for spoken answers.</summary>
/// <param name="Id">A stable identifier: what <see cref="VoiceSettings.TextToSpeechModelId"/> stores.</param>
/// <param name="DisplayName">What the user is shown.</param>
/// <param name="Summary">One line that says what sets the model apart.</param>
public sealed record TextToSpeechModel(string Id, string DisplayName, string Summary);

/// <summary>The text-to-speech models the Settings window offers (PROJECT_SPEC §4.2 Speech); <c>Assistant.Voice</c> runs each of them locally.</summary>
public static class TextToSpeechModels
{
    /// <summary>The model chosen until the user chooses another.</summary>
    public const string DefaultId = "kitten-tts-mini";

    /// <summary>A very small model, about 80 million parameters.</summary>
    public static TextToSpeechModel KittenTtsMini { get; } = new(
        DefaultId, "KittenTTS Mini 0.8 (80M)", "Very small and quick, for any PC.");

    /// <summary>A small model of about 82 million parameters, run as an ONNX file.</summary>
    public static TextToSpeechModel Kokoro { get; } = new(
        "kokoro-82m-onnx", "Kokoro-82M ONNX", "A small model with natural-sounding voices.");

    /// <summary>The Piper engine and its voices.</summary>
    public static TextToSpeechModel Piper { get; } = new(
        "piper", "Piper", "Many voices and languages, fast on the CPU.");

    /// <summary>Every model, the default first.</summary>
    public static IReadOnlyList<TextToSpeechModel> All { get; } = [KittenTtsMini, Kokoro, Piper];

    public static TextToSpeechModel CustomApi { get; } = new("custom-api", "Custom speech API", "An OpenAI-compatible speech endpoint.");

    /// <summary>The model with identifier <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    public static TextToSpeechModel? Find(string? id) =>
        id == CustomApi.Id ? CustomApi : All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));
}
