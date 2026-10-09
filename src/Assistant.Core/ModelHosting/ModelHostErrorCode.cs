using System.Text.Json.Serialization;

namespace Assistant.Core.ModelHosting;

/// <summary>Why a request to the model host failed (<see cref="ModelHostError"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelHostErrorCode>))]
public enum ModelHostErrorCode
{
    /// <summary>The host failed in a way it did not expect. The reason is in its log, without content.</summary>
    Internal = 0,

    /// <summary>The frame's <c>v</c> is not <see cref="ModelHostProtocol.Version"/>.</summary>
    UnsupportedProtocolVersion = 1,

    /// <summary>The frame's <c>type</c> is not a message the receiver accepts.</summary>
    UnknownMessageType = 2,

    /// <summary>The frame is not valid JSON, or the message's fields are missing or out of range.</summary>
    MalformedMessage = 3,

    /// <summary>This host build does not do what the request asks yet.</summary>
    NotImplemented = 4,

    /// <summary>No installed model has the requested identifier.</summary>
    ModelNotFound = 5,

    /// <summary>The model's files could not be loaded, for example because memory ran out.</summary>
    ModelLoadFailed = 6,

    /// <summary>The model does not accept images.</summary>
    VisionNotSupported = 7,

    /// <summary>Another generation is running; the host runs one at a time.</summary>
    Busy = 8,

    /// <summary>The host stopped the request because it is shutting down.</summary>
    ShuttingDown = 9,

    /// <summary>
    /// The request was stopped before it finished because a later request replaced it, for example a load that another
    /// load or an unload superseded.
    /// </summary>
    Cancelled = 10,

    /// <summary>The bundled inference runtime cannot run; the health report's runtime state says why.</summary>
    RuntimeUnavailable = 11,

    /// <summary>
    /// The engine could not generate the answer: it refused the prompt, broke off, or stopped while it generated.
    /// </summary>
    GenerationFailed = 12,

    /// <summary>The prompt is longer than the loaded model's context window, so nothing was generated.</summary>
    ContextExceeded = 13,

    /// <summary>
    /// The app found, before asking the host, that the user has paused the local AI (<see cref="ILocalAiPause"/>), so the model is not
    /// loaded. The host never sends it.
    /// </summary>
    Paused = 14,

    /// <summary>
    /// The app found, before asking the host, that a file of the model that came packaged with the Assistant is missing or does not match the
    /// size or SHA-256 the package lists (<c>IPackagedAssets</c>), so the model is not loaded. The host never sends it.
    /// </summary>
    FilesFailedCheck = 15,

    /// <summary>
    /// The app found, before asking the host, that game mode has paused the local AI because a game is running (<see cref="ILocalAiPause"/>,
    /// <see cref="LocalAiPauseReason.GameMode"/>), so the model is not loaded. The host never sends it.
    /// </summary>
    PausedForGame = 16,
}
