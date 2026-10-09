using System.Text.Json.Serialization;

namespace Assistant.Core.Contracts;

/// <summary>Why the local model is <see cref="ModelStatus.Failed"/>, as far as the user needs to be told.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelFailure>))]
public enum ModelFailure
{
    /// <summary>No model file is set up, or a file it needs is missing.</summary>
    ModelNotFound = 0,

    /// <summary>The inference runtime bundled with the app cannot run.</summary>
    RuntimeUnavailable = 1,

    /// <summary>The model's files could not be loaded, for example because a file is damaged or memory ran out.</summary>
    LoadFailed = 2,

    /// <summary>The engine loaded the model, then kept exiting, and was not started again.</summary>
    EngineStopped = 3,

    /// <summary>The model host process could not be started, or went away.</summary>
    HostUnavailable = 4,
}
