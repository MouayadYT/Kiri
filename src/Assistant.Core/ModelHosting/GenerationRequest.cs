using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// A prompt for the model to answer: <see cref="GenerateTextRequest"/> or <see cref="GenerateMultimodalRequest"/>.
/// </summary>
/// <remarks>
/// The host generates with the model it has loaded, which must be <see cref="ModelId"/>. Until it has a catalog to
/// load a model by its identifier, the owner loads the model first (<see cref="LoadModelRequest"/>, with its files),
/// and a request for a model that is not loaded is answered with <see cref="ModelHostErrorCode.ModelNotFound"/>. The
/// host runs one generation at a time and answers another that arrives meanwhile with
/// <see cref="ModelHostErrorCode.Busy"/>. It answers with a stream of <see cref="TextDelta"/> and
/// <see cref="ToolCallGenerated"/> replies that ends with <see cref="GenerationEnded"/>, or with an error such as
/// <see cref="ModelHostErrorCode.ContextExceeded"/> or <see cref="ModelHostErrorCode.GenerationFailed"/>, which may
/// follow some text. <see cref="CancelGenerationRequest"/> stops it early.
/// </remarks>
/// <param name="ModelId">The model to generate with.</param>
/// <param name="Instructions">System instructions that precede the conversation.</param>
/// <param name="Messages">
/// The conversation, oldest first, as the orchestrator assembled it. Untrusted context is already delimited inside
/// the message text.
/// </param>
public abstract record GenerationRequest(string ModelId, string Instructions, IReadOnlyList<PromptMessage> Messages)
    : ModelHostRequest
{
    private readonly IReadOnlyList<ToolDefinition> _tools = [];

    /// <summary>Tools the model may call. Empty when tool calling is off for this generation.</summary>
    /// <remarks>A missing or null list reads as empty, as the JSON source generator sets every init property.</remarks>
    public IReadOnlyList<ToolDefinition> Tools
    {
        get => _tools;
        init => _tools = value ?? [];
    }

    /// <summary>The most tokens to generate, or <see langword="null"/> for the model's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>How freely the model chooses its words, from 0 to 2, or <see langword="null"/> for the engine's default.</summary>
    public double? Temperature { get; init; }

    internal override bool IsWellFormed() =>
        !string.IsNullOrWhiteSpace(ModelId)
        && Instructions is not null
        && Messages is { Count: > 0 }
        && Messages.All(message => message is not null && message.IsWellFormed())
        && Tools.All(tool => tool is not null && !string.IsNullOrWhiteSpace(tool.Name) && tool.InputSchemaJson is not null)
        && MaxOutputTokens is null or > 0
        && Temperature is null or (>= 0 and <= 2);

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"ModelId = {ModelId}, Messages = {Messages?.Count}, Tools = {Tools.Count}, " +
            $"MaxOutputTokens = {MaxOutputTokens}, Temperature = {Temperature}");
        return true;
    }
}
