using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Contracts;

/// <summary>
/// The one place context comes in and goes out (PROJECT_SPEC §5.5). Whatever supplies context for a conversation (files
/// the user attached, selected text, a screenshot, the Files scope's search results, and later a tool's results) gives
/// it to <see cref="Add"/> with where it came from; the service keeps it apart for each conversation, merges duplicates,
/// remembers every supply's provenance, hands the items that wait for the next question to whoever asks it
/// (<see cref="PendingItems"/>), and fits the context into the model's window by the fixed priority rules
/// (<see cref="Prepare"/>, which uses the <see cref="ContextBudgeter"/>), remembering what did not fit so that it can be
/// shown (<see cref="GetContext"/>).
/// </summary>
/// <remarks>
/// It holds what it is given in memory only, never saves or logs it, and keeps an item's text or pixels only until the
/// item has been sent with a question, when the conversation's message carries them. It does not read files, call the
/// model or ask for permissions: whoever supplies an item has already been allowed to.
/// </remarks>
public interface IContextService
{
    /// <summary>
    /// Accepts <paramref name="item"/> as context for the next question of a conversation. An item with the same
    /// content as one already waiting is merged with it; one with the same content as one already sent is not added.
    /// </summary>
    /// <param name="conversationId">The conversation the context is for.</param>
    /// <param name="item">
    /// The item. Its <see cref="ContextItem.Source"/> says how it came in; when it does not, its kind does (see
    /// <see cref="ContextPriorityRules.SourceOf"/>).
    /// </param>
    /// <param name="origin">
    /// A short, fixed label for whoever supplies it, such as <c>composer</c>: recorded as its provenance, so it must
    /// never hold anything the user wrote, or a path.
    /// </param>
    ContextAddResult Add(Guid conversationId, ContextItem item, string origin);

    /// <summary>
    /// Takes back an item that waits for the next question, such as one a question did not use. Returns
    /// <see langword="false"/> when there is no such item, or it has been sent: what a conversation has carried stays.
    /// </summary>
    bool Remove(Guid conversationId, Guid itemId);

    /// <summary>
    /// The items that wait for the next question, in the order they are laid out in the prompt: by rank, the highest
    /// first, and within a rank in the order they came. Nothing is taken: they wait until <see cref="Commit"/> says they
    /// were sent.
    /// </summary>
    IReadOnlyList<ContextItem> PendingItems(Guid conversationId);

    /// <summary>
    /// Records that <paramref name="items"/> were sent with a question, which the conversation now carries: they no
    /// longer wait, are compared with what is added later, and keep only their descriptors here. Items this service does
    /// not hold are ignored.
    /// </summary>
    void Commit(Guid conversationId, IReadOnlyList<ContextItem> items);

    /// <summary>The conversation's context as it is now, including what the last prompt fitted left out or cut short.</summary>
    ConversationContext GetContext(Guid conversationId);

    /// <summary>
    /// Fits <paramref name="conversation"/> into <paramref name="model"/>'s context window with the
    /// <see cref="ContextBudgeter"/>, serving context by the priority rules (<see cref="ContextPriorityRules"/>), and
    /// remembers what became of each item for <see cref="GetContext"/>. The conversation is not changed.
    /// </summary>
    /// <param name="conversationId">The conversation whose context it is, or one the service holds nothing for.</param>
    /// <param name="instructions">The system instructions exactly as they are sent: never cut.</param>
    /// <param name="conversation">The conversation, oldest first, ending with the user's message to answer.</param>
    /// <param name="model">The model, whose context length is the window.</param>
    /// <param name="limits">The user's limits.</param>
    /// <exception cref="ArgumentException"><paramref name="conversation"/> does not end with a user message.</exception>
    BudgetedConversation Prepare(
        Guid conversationId,
        string instructions,
        IReadOnlyList<Message> conversation,
        ModelInfo model,
        ContextLimitSettings limits);

    /// <summary>Forgets everything held for a conversation, such as when it is deleted or its session is dropped.</summary>
    void Forget(Guid conversationId);
}
