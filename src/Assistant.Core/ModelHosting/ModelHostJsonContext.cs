using System.Text.Json.Serialization;

namespace Assistant.Core.ModelHosting;

/// <summary>Source-generated JSON metadata for the bodies of the model-host protocol's messages.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthRequest))]
[JsonSerializable(typeof(LoadModelRequest))]
[JsonSerializable(typeof(UnloadModelRequest))]
[JsonSerializable(typeof(GenerateTextRequest))]
[JsonSerializable(typeof(GenerateMultimodalRequest))]
[JsonSerializable(typeof(CancelGenerationRequest))]
[JsonSerializable(typeof(ShutdownRequest))]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(ModelLoadProgress))]
[JsonSerializable(typeof(ModelLoaded))]
[JsonSerializable(typeof(ModelUnloaded))]
[JsonSerializable(typeof(TextDelta))]
[JsonSerializable(typeof(ToolCallGenerated))]
[JsonSerializable(typeof(GenerationEnded))]
[JsonSerializable(typeof(ShutdownAccepted))]
[JsonSerializable(typeof(ModelStatusReport))]
[JsonSerializable(typeof(ModelHostError))]
internal sealed partial class ModelHostJsonContext : JsonSerializerContext;
