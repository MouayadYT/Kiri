namespace Assistant.Core.ModelHosting;

/// <summary>Generates a text answer, and any tool calls, for a prompt of text only.</summary>
/// <inheritdoc cref="GenerationRequest"/>
public sealed record GenerateTextRequest(string ModelId, string Instructions, IReadOnlyList<PromptMessage> Messages)
    : GenerationRequest(ModelId, Instructions, Messages);
