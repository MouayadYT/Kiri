using System.Text;

namespace Assistant.Core.Domain;

/// <summary>One incremental piece of a streamed assistant response.</summary>
/// <remarks>
/// Only the members that match <see cref="Type"/> are set. Prefer the factory methods, which always create
/// well-formed chunks.
/// </remarks>
/// <param name="Type">What the chunk carries.</param>
public sealed record AssistantResponseChunk(AssistantResponseChunkType Type)
{
    /// <summary>
    /// Text of a <see cref="AssistantResponseChunkType.TextDelta"/>, <see cref="AssistantResponseChunkType.Notice"/> or
    /// <see cref="AssistantResponseChunkType.ContextWarning"/> chunk.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>The requested call of a <see cref="AssistantResponseChunkType.ToolCall"/> chunk.</summary>
    public ToolCall? ToolCall { get; init; }

    /// <summary>The outcome of a <see cref="AssistantResponseChunkType.ToolResult"/> chunk.</summary>
    public ToolResult? ToolResult { get; init; }

    /// <summary>The grounding files of a <see cref="AssistantResponseChunkType.Sources"/> chunk.</summary>
    public IReadOnlyList<SearchResultItem> Sources { get; init; } = [];

    /// <summary>The offer of an <see cref="AssistantResponseChunkType.IntegrationOffer"/> chunk.</summary>
    public IntegrationOffer? Offer { get; init; }

    /// <summary>
    /// For an <see cref="AssistantResponseChunkType.IntegrationOffer"/> chunk, the request that was set aside until the user decides on the offer,
    /// which the Assistant goes back to when they have (step 110); <see langword="null"/> when there is nothing to go back to.
    /// </summary>
    public PendingRequest? Pending { get; init; }

    /// <summary>Creates a chunk that appends <paramref name="text"/> to the answer.</summary>
    public static AssistantResponseChunk ForTextDelta(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(AssistantResponseChunkType.TextDelta) { Text = text };
    }

    /// <summary>Creates a chunk that reports a tool call requested by the model.</summary>
    public static AssistantResponseChunk ForToolCall(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return new(AssistantResponseChunkType.ToolCall) { ToolCall = toolCall };
    }

    /// <summary>Creates a chunk that reports a finished tool call.</summary>
    public static AssistantResponseChunk ForToolResult(ToolResult toolResult)
    {
        ArgumentNullException.ThrowIfNull(toolResult);
        return new(AssistantResponseChunkType.ToolResult) { ToolResult = toolResult };
    }

    /// <summary>Creates a chunk that lists the files the answer is grounded in.</summary>
    public static AssistantResponseChunk ForSources(IReadOnlyList<SearchResultItem> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new(AssistantResponseChunkType.Sources) { Sources = sources };
    }

    /// <summary>Creates a chunk with a notice the UI must show.</summary>
    public static AssistantResponseChunk ForNotice(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(AssistantResponseChunkType.Notice) { Text = text };
    }

    /// <summary>Creates a chunk with a warning that context was left out or cut short because of the token budget.</summary>
    public static AssistantResponseChunk ForContextWarning(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(AssistantResponseChunkType.ContextWarning) { Text = text };
    }

    /// <summary>
    /// Creates a chunk that offers to install an integration, for the user to approve or turn down. <paramref name="pending"/> is the request the
    /// offer was made for, when there is one: what the user decides goes back to it.
    /// </summary>
    public static AssistantResponseChunk ForIntegrationOffer(IntegrationOffer offer, PendingRequest? pending = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new(AssistantResponseChunkType.IntegrationOffer) { Offer = offer, Pending = pending };
    }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Type = {Type}");
        return true;
    }
}
