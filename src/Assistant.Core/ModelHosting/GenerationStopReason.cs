using System.Text.Json.Serialization;

namespace Assistant.Core.ModelHosting;

/// <summary>Why a generation stopped (<see cref="GenerationEnded"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GenerationStopReason>))]
public enum GenerationStopReason
{
    /// <summary>The model finished its answer.</summary>
    Completed = 0,

    /// <summary>The answer reached <see cref="GenerationRequest.MaxOutputTokens"/> or the context window.</summary>
    OutputLimit = 1,

    /// <summary>A <see cref="CancelGenerationRequest"/> or a shutdown stopped it.</summary>
    Cancelled = 2,
}
