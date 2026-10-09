using System.Text;

namespace Assistant.Core.Domain;

/// <summary>An ordered list of messages with a title and timestamps.</summary>
/// <param name="Id">Unique identifier of the conversation.</param>
/// <param name="Title">Display title. It may be empty until one is set.</param>
/// <param name="CreatedAt">When the conversation started.</param>
/// <param name="UpdatedAt">When the conversation last changed.</param>
public sealed record Conversation(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Messages, oldest first.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, CreatedAt = {CreatedAt:O}, UpdatedAt = {UpdatedAt:O}, Messages = {Messages.Count}");
        return true;
    }
}
