using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Local conversation history.</summary>
/// <remarks>
/// Writes nothing while history is turned off, and never stores context content, only context descriptors
/// (PROJECT_SPEC §3.5). A conversation grows a message at a time (<see cref="SaveMessageAsync"/>) instead of being
/// written again as a whole.
/// </remarks>
public interface IConversationService
{
    /// <summary>The most results <see cref="SearchAsync"/> returns unless told otherwise.</summary>
    const int DefaultSearchLimit = 100;

    /// <summary>
    /// Lists saved conversations, most recently updated first, without their messages, each with what a card or a row
    /// shows of them.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a conversation with its messages, or returns <see langword="null"/> when it does not exist.</summary>
    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds conversations whose title or messages match <paramref name="query"/>, most recently updated first, without
    /// their messages. It searches the device's own database and never asks a model.
    /// </summary>
    /// <remarks>
    /// Every word of the query must be found, in the title or in any message of the conversation: a word matches text
    /// that begins with it, without regard to case or accents, and a phrase in quotes matches those words together. A
    /// query with no words finds nothing.
    /// </remarks>
    /// <param name="query">What the user typed.</param>
    /// <param name="limit">The most conversations to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(
        string query, int limit = DefaultSearchLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a conversation and all of its messages, replacing any stored version. It rewrites the whole conversation, so
    /// a conversation that is going on is saved with <see cref="SaveMessageAsync"/> instead.
    /// </summary>
    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves one message of a conversation and leaves the rest as they are: a message that is not stored yet is added
    /// after the conversation's last, and one that is stored (the same <see cref="Message.Id"/>) is changed where it is,
    /// as when an answer that was saved when it began is saved again when it ends. A conversation that does not exist
    /// yet is created, starting when the message was created, and one with no title gets a provisional title from the
    /// first request the user made in it.
    /// </summary>
    /// <param name="conversationId">The conversation the message belongs to.</param>
    /// <param name="message">The message. Only the descriptors of its context items are kept.</param>
    /// <param name="changedAt">When the conversation changed, which is when it was last updated.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    Task SaveMessageAsync(
        Guid conversationId, Message message, DateTimeOffset changedAt, CancellationToken cancellationToken = default);

    /// <summary>Changes the title of a saved conversation.</summary>
    Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default);

    /// <summary>Deletes a conversation, its messages and its search-index entries.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes every saved conversation, and the database's backups, which are copies of them.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}
