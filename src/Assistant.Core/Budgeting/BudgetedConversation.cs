using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Budgeting;

/// <summary>A conversation fitted into the context window, and what the user must be told about it.</summary>
/// <param name="Messages">
/// The messages to build the prompt from, oldest first, ending with the message being answered. It is the conversation
/// itself when everything fits, and otherwise a shorter copy whose context items may be shortened or missing; the
/// conversation it was made from is never changed.
/// </param>
/// <param name="MaxOutputTokens">The most the model is asked to write: the tokens reserved for the answer.</param>
/// <param name="Notices">What was left out or cut short, for the user to see (PROJECT_SPEC §4.4). Empty when nothing was.</param>
/// <param name="Report">The counts.</param>
public sealed record BudgetedConversation(
    IReadOnlyList<Message> Messages,
    int MaxOutputTokens,
    IReadOnlyList<string> Notices,
    ContextBudgetReport Report)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Messages = {Messages.Count}, MaxOutputTokens = {MaxOutputTokens}, Notices = {Notices.Count}");
        return true;
    }
}
