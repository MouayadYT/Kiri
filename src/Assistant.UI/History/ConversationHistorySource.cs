using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Data;
using Assistant.UI.ViewModels;

namespace Assistant.UI.History;

/// <summary>
/// The History window's conversations from the local history (<see cref="IConversationService"/>): the list without the
/// messages, a conversation's messages when it is opened, and the conversations a search finds. The database is read
/// away from the UI thread; the view models are made where the call was made.
/// </summary>
internal sealed class ConversationHistorySource(IConversationService history, MessageMapper mapper) : IHistorySource
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<HistoryConversation>> ListAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<HistoryConversation>>(async () =>
        {
            var summaries = await history.ListAsync(cancellationToken).ConfigureAwait(true);
            return [.. summaries.Select(Listed)];
        });

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageViewModel>?> LoadMessagesAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<MessageViewModel>?>(async () =>
        {
            var conversation = await history.GetAsync(id, cancellationToken).ConfigureAwait(true);
            return conversation is null ? null : [.. conversation.Messages.Select(mapper.ToViewModel)];
        });

    /// <inheritdoc/>
    public Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<HistorySearchHit>>(async () =>
        {
            var results = await history.SearchAsync(query, cancellationToken: cancellationToken).ConfigureAwait(true);
            return
            [
                .. results.Select(result => new HistorySearchHit(
                    Listed(result.Conversation), result.Snippet?.Text, result.Snippet?.Matches, result.Snippet?.MessageId)),
            ];
        });

    // Reads the history, and says that it cannot be read (HistoryUnavailableException) when the database fails, which is
    // what the History window expects of any source.
    private static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
        catch (DatabaseException exception)
        {
            throw new HistoryUnavailableException(exception);
        }
    }

    // A conversation as its card shows it; its messages are not loaded.
    private static HistoryConversation Listed(ConversationSummary summary) =>
        new(summary.Id, null, summary.UpdatedAt, summary.Title.Length == 0 ? null : summary.Title)
        {
            LatestAnswerText = summary.LatestAnswerText,
            Image = MessageMapper.ToImage(summary.LatestImage),
        };
}
