using System.Text;

namespace Assistant.Core.Domain;

/// <summary>
/// A short piece of one message, shown under a search result so the user sees why the conversation matched: the words
/// around the first match, on one line, with the matches marked.
/// </summary>
/// <param name="MessageId">The message the text comes from.</param>
/// <param name="Text">The text, on one line, cut short with an ellipsis where it does not begin or end the message.</param>
/// <param name="Matches">Where the searched words are in <paramref name="Text"/>, in order.</param>
public sealed record SearchSnippet(Guid MessageId, string Text, IReadOnlyList<TextMatch> Matches)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"MessageId = {MessageId}, Matches = {Matches.Count}");
        return true;
    }
}
