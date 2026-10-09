using System.Text;

namespace Assistant.Core.Domain;

/// <summary>One user turn submitted to the assistant.</summary>
/// <param name="ConversationId">The conversation this turn belongs to.</param>
/// <param name="Prompt">Text the user typed. It may be empty when an action supplies the instruction.</param>
public sealed record AssistantRequest(Guid ConversationId, string Prompt)
{
    /// <summary>Earlier messages of the conversation, oldest first, for follow-up turns.</summary>
    public IReadOnlyList<Message> History { get; init; } = [];

    /// <summary>
    /// Context the user scoped for this turn. A <see cref="ContextItemType.SearchResults"/> item turns on the Files
    /// scope.
    /// </summary>
    public IReadOnlyList<ContextItem> ContextItems { get; init; } = [];

    /// <summary>Identifier of the action the user picked, or <see langword="null"/> for a free-form ask.</summary>
    public string? ActionId { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"ConversationId = {ConversationId}, ActionId = {ActionId}, " +
            $"History = {History.Count}, ContextItems = {ContextItems.Count}");
        return true;
    }
}
