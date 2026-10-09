using System.Text;
using Assistant.Core.Budgeting;

namespace Assistant.Core.Context;

/// <summary>The context of one conversation as it is now: what waits for the next question, and what earlier questions carry.</summary>
/// <param name="Pending">Items that wait for the next question, in the order they are laid out in the prompt: highest rank first.</param>
/// <param name="Earlier">Items already sent with earlier questions, highest rank first, then the newest question's first.</param>
public sealed record ConversationContext(IReadOnlyList<ContextEntry> Pending, IReadOnlyList<ContextEntry> Earlier)
{
    /// <summary>The context of a conversation that has none.</summary>
    public static ConversationContext Empty { get; } = new([], []);

    /// <summary>Every item, the pending ones first.</summary>
    public IEnumerable<ContextEntry> All => Pending.Concat(Earlier);

    /// <summary>How many items the last prompt fitted left out, for the user to be warned about.</summary>
    public int LeftOutCount => All.Count(entry => entry.LastFit == ContextFate.LeftOut);

    /// <summary>How many items the last prompt fitted cut short.</summary>
    public int ShortenedCount => All.Count(entry => entry.LastFit == ContextFate.Shortened);

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Pending = {Pending.Count}, Earlier = {Earlier.Count}");
        return true;
    }
}
