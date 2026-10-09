using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Context;

/// <summary>What happened to an item given to <see cref="Contracts.IContextService.Add"/>.</summary>
/// <param name="Outcome">What was done with it.</param>
/// <param name="Item">
/// The item as the context service holds it, with its <see cref="ContextItem.Source"/> set: the one to use in place of the
/// item given when it was <see cref="ContextAddOutcome.Merged"/> (the first of the same content) or
/// <see cref="ContextAddOutcome.AlreadyInConversation"/> (only its descriptor is kept). For
/// <see cref="ContextAddOutcome.Rejected"/> it is the item given.
/// </param>
public sealed record ContextAddResult(ContextAddOutcome Outcome, ContextItem Item)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Outcome = {Outcome}");
        return true;
    }
}
