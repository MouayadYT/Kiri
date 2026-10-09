using System.Text.Json.Serialization;

namespace Assistant.Core.Contracts;

/// <summary>Availability of the local model (PROJECT_SPEC §5.6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelStatus>))]
public enum ModelStatus
{
    /// <summary>No usable model is installed, so AI surfaces show the "model not available" state.</summary>
    NotInstalled = 0,

    /// <summary>A model is installed but not loaded, or it was unloaded. It loads on first use.</summary>
    NotLoaded = 1,

    /// <summary>The model is loading.</summary>
    Loading = 2,

    /// <summary>The model is loaded and ready.</summary>
    Ready = 3,

    /// <summary>The model failed to load, or its engine stopped; <see cref="ModelFailure"/> says why.</summary>
    Failed = 4,

    /// <summary>The model is being unloaded and its memory freed.</summary>
    Unloading = 5,
}
