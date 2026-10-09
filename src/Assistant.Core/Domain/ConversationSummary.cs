using System.Text;

namespace Assistant.Core.Domain;

/// <summary>
/// A saved conversation as a history list shows it: its title and times, and what a card or a row needs of its
/// messages, without the messages themselves.
/// </summary>
/// <param name="Id">Unique identifier of the conversation.</param>
/// <param name="Title">Display title. It may be empty for a conversation that has none yet.</param>
/// <param name="CreatedAt">When the conversation started.</param>
/// <param name="UpdatedAt">When the conversation last changed.</param>
/// <param name="MessageCount">How many messages the conversation holds.</param>
public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount)
{
    /// <summary>
    /// The text of the conversation's latest assistant message that has any, as it was written (Markdown included) and cut
    /// off after a few thousand characters, or <see langword="null"/> when no answer has any. A card shows it as a preview.
    /// </summary>
    public string? LatestAnswerText { get; init; }

    /// <summary>
    /// The descriptor of the image most recently attached to the conversation, in whichever message, or
    /// <see langword="null"/> when none was. Only its name and where the file is: the image is read again from there.
    /// </summary>
    public ContextItem? LatestImage { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, CreatedAt = {CreatedAt:O}, UpdatedAt = {UpdatedAt:O}, MessageCount = {MessageCount}");
        return true;
    }
}
