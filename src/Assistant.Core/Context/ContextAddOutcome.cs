namespace Assistant.Core.Context;

/// <summary>What <see cref="Contracts.IContextService.Add"/> did with an item.</summary>
public enum ContextAddOutcome
{
    /// <summary>The item is new and waits for the next question.</summary>
    Added = 0,

    /// <summary>
    /// The same content was already waiting for the next question, so the two are one item: its provenance lists both
    /// supplies, and it ranks as the higher-ranked of them.
    /// </summary>
    Merged = 1,

    /// <summary>
    /// The same content was already sent with an earlier question of this conversation, which still carries it, so it is
    /// not sent again.
    /// </summary>
    AlreadyInConversation = 2,

    /// <summary>
    /// Too many items wait for the next question (<see cref="ContextService.MaxPendingItems"/>), so this one was not
    /// accepted.
    /// </summary>
    Rejected = 3,
}
