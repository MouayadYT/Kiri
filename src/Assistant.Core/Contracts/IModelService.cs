using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Runs local inference, which happens in the app's own model host process (PROJECT_SPEC §5.6).</summary>
public interface IModelService
{
    /// <summary>
    /// Gets the model used for generation, or <see langword="null"/> when no usable model is installed.
    /// </summary>
    Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the model's response as <see cref="AssistantResponseChunkType.TextDelta"/> and
    /// <see cref="AssistantResponseChunkType.ToolCall"/> chunks, loading the model first if needed.
    /// </summary>
    IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
