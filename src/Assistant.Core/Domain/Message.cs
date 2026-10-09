using System.Text;

namespace Assistant.Core.Domain;

/// <summary>One entry in a <see cref="Conversation"/>.</summary>
/// <param name="Id">Unique identifier of the message.</param>
/// <param name="Role">Who authored the message.</param>
/// <param name="Text">Message text. It may be empty, for example for an assistant message that only calls tools.</param>
/// <param name="CreatedAt">When the message was created.</param>
public sealed record Message(Guid Id, MessageRole Role, string Text, DateTimeOffset CreatedAt)
{
    /// <summary>Context attached to a <see cref="MessageRole.User"/> message.</summary>
    public IReadOnlyList<ContextItem> ContextItems { get; init; } = [];

    /// <summary>Tool calls requested by a <see cref="MessageRole.Assistant"/> message.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>The result carried by a <see cref="MessageRole.Tool"/> message; otherwise <see langword="null"/>.</summary>
    public ToolResult? ToolResult { get; init; }

    /// <summary>Files a <see cref="MessageRole.Assistant"/> message is grounded in.</summary>
    public IReadOnlyList<SearchResultItem> Sources { get; init; } = [];

    /// <summary>
    /// The structured parts of a <see cref="MessageRole.Assistant"/> message that are not prose, in order, each placed
    /// among the prose by its <see cref="CardMetadata.TextOffset"/>.
    /// </summary>
    public IReadOnlyList<CardMetadata> Cards { get; init; } = [];

    /// <summary>How an assistant message ended. Other messages are always <see cref="MessageOutcome.Complete"/>.</summary>
    public MessageOutcome Outcome { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Role = {Role}, CreatedAt = {CreatedAt:O}");
        return true;
    }
}
