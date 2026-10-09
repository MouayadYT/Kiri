namespace Assistant.Core.Contracts;

/// <summary>Identity and capabilities of a local model.</summary>
/// <param name="Id">Model identifier from its manifest.</param>
/// <param name="ContextLength">Context window in tokens, shared by the prompt and the output.</param>
public sealed record ModelInfo(string Id, int ContextLength)
{
    /// <summary>Whether the model can emit tool calls.</summary>
    public bool SupportsToolCalling { get; init; }

    /// <summary>Whether the model accepts images.</summary>
    public bool SupportsVision { get; init; }

    /// <summary>Whether the engine can constrain the model's output to JSON or a grammar.</summary>
    public bool SupportsConstrainedOutput { get; init; }
}
