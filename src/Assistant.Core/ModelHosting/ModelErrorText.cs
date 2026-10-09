using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// What to tell the user when the local model could not answer: one plain sentence for a
/// <see cref="ModelHostException"/>, and for a load that failed the same words the model's status uses
/// (<see cref="ModelStatusText"/>). It never holds a path or content, so it is also safe in diagnostics.
/// </summary>
public static class ModelErrorText
{
    /// <summary>What the Assistant says while the user has paused the local AI (<see cref="ILocalAiPause"/>).</summary>
    public const string PausedText = "Local AI is paused. Choose Resume Local AI from the Assistant's icon in the notification area to use it again.";

    /// <summary>What the Assistant says while game mode has paused the local AI because a game, or a creative app it watches for, is running.</summary>
    public const string PausedForGameText =
        "Game mode paused the local AI while your game or creative app is running, and it comes back by itself when that closes. To use it now, choose Resume Local AI from the Assistant's icon in the notification area.";

    /// <summary>Describes why <paramref name="exception"/>'s request failed.</summary>
    public static string Describe(ModelHostException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Code switch
        {
            // No code: the host could not be started, or it went away while it worked.
            null => "The local model process stopped or couldn't be started. Try again.",
            ModelHostErrorCode.ModelNotFound => ModelStatusText.Describe(ModelStatus.Failed, ModelFailure.ModelNotFound),
            ModelHostErrorCode.ModelLoadFailed => ModelStatusText.Describe(ModelStatus.Failed, ModelFailure.LoadFailed),
            ModelHostErrorCode.RuntimeUnavailable =>
                ModelStatusText.Describe(ModelStatus.Failed, ModelFailure.RuntimeUnavailable),
            ModelHostErrorCode.ContextExceeded => "That's more than the local model can read at once. Try something shorter.",
            ModelHostErrorCode.GenerationFailed => "The local model couldn't finish its answer. Try again.",
            ModelHostErrorCode.Busy => "The local model is still busy with another answer. Try again in a moment.",
            ModelHostErrorCode.Cancelled => "Another request to the local model replaced this one.",
            ModelHostErrorCode.ShuttingDown => "The local model was shutting down. Try again.",
            ModelHostErrorCode.VisionNotSupported => "The local model can't read images.",
            ModelHostErrorCode.Paused => PausedText,
            ModelHostErrorCode.PausedForGame => PausedForGameText,
            ModelHostErrorCode.FilesFailedCheck => Assets.AssetStatusText.ModelFilesFailedText,
            ModelHostErrorCode.NotImplemented => "The local model can't do that yet.",
            _ => "The local model couldn't answer. Try again.",
        };
    }
}
