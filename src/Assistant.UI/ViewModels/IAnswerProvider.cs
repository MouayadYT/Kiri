using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// Answers a question asked in the floating conversation with the Assistant's message: typed content parts, each chosen
/// by what produced it, so the conversation draws every part by its kind without guessing from text.
/// </summary>
public interface IAnswerProvider
{
    /// <summary>
    /// Returns the Assistant's message answering <paramref name="question"/>, or <see langword="null"/> when there is
    /// nothing to show.
    /// </summary>
    MessageViewModel? Answer(string question);

    /// <summary>
    /// Returns the Assistant's message answering <paramref name="question"/> once it is ready, for an answer that takes
    /// time: a provider that searches, thinks or runs tools reports it to the activity tracker, which shows the
    /// Searching chip, and stops when <paramref name="cancellationToken"/> is cancelled. Without an override it answers
    /// at once with <see cref="Answer"/>.
    /// </summary>
    Task<MessageViewModel?> AnswerAsync(string question, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(question));

    /// <summary>
    /// Answers <paramref name="question"/> as the answer comes, the way the model streams one: hands the Assistant's
    /// message to <paramref name="show"/> once it has something to show, and goes on adding to it until the returned
    /// task completes. <paramref name="show"/> is called at most once, on the calling thread (the UI thread), and the
    /// message is only added to after that on the same thread. Cancelling stops the answer and leaves what was shown;
    /// a streamed answer is marked <see cref="MessageStatus.Stopped"/>, and may be shown as it stops, even with nothing
    /// in it, so the conversation says it was stopped.
    /// Without an override it shows the message <see cref="AnswerAsync(string, CancellationToken)"/> returns, once it
    /// is ready.
    /// </summary>
    async Task StreamAnswerAsync(string question, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(show);
        if (await AnswerAsync(question, cancellationToken).ConfigureAwait(true) is { } answer)
        {
            show(answer);
        }
    }

    /// <summary>
    /// Answers <paramref name="question"/> as the next message of the conversation <paramref name="conversationId"/>,
    /// as <see cref="StreamAnswerAsync(string, Action{MessageViewModel}, CancellationToken)"/> does. A provider that
    /// remembers a conversation, so its answer takes the earlier turns into account, keeps what it needs by this id;
    /// the same id is used for every question of the same conversation, and a conversation that was moved to the History
    /// window keeps it. Without an override the question is answered without any earlier ones.
    /// </summary>
    Task StreamAnswerAsync(
        Guid conversationId, string question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync(question, show, cancellationToken);

    /// <summary>
    /// Tells the provider what the conversation <paramref name="conversationId"/> held before the question about to be asked, when the
    /// conversation is one it has not been asked in, such as one opened from the saved history after the app was restarted: a provider
    /// that remembers conversations takes what it needs from <paramref name="earlier"/> so that the question is answered with what was
    /// said before. Without an override nothing is done.
    /// </summary>
    void Resume(Guid conversationId, IReadOnlyList<MessageViewModel> earlier)
    {
    }

    /// <summary>
    /// Lets go of the picture of the context item <paramref name="contextItemId"/> (a screenshot) that the conversation
    /// <paramref name="conversationId"/> remembers, because the user took it off: a provider that keeps what it was asked about
    /// drops the pixels, so that the memory is freed. Without an override nothing is done.
    /// </summary>
    void ReleaseContext(Guid conversationId, Guid contextItemId)
    {
    }

    /// <summary>
    /// Answers the user's message <paramref name="question"/> as the next message of the conversation
    /// <paramref name="conversationId"/>, as the overload with its text does. A provider may add to the message what the
    /// question came to be about, such as an image it asked the user for (<see cref="MessageViewModel.Attach"/>).
    /// Without an override the message's text is answered.
    /// </summary>
    Task StreamAnswerAsync(
        Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        return StreamAnswerAsync(conversationId, question.Text, show, cancellationToken);
    }

    /// <summary>
    /// Goes on with what an answer of this provider set aside, as the next answer of the conversation <paramref name="conversationId"/> (PROJECT_SPEC §4.8,
    /// step 110): the request the user made that needed an integration, which is carried out now that it is installed, or said not to have been when it
    /// is not. The user's message is not added again: the request is the one already in the conversation. <paramref name="show"/> and
    /// <paramref name="cancellationToken"/> are as for <see cref="StreamAnswerAsync(Guid, string, Action{MessageViewModel}, CancellationToken)"/>.
    /// Without an override nothing is done, since a provider that set nothing aside has nothing to go back to.
    /// </summary>
    Task StreamContinuationAsync(
        Guid conversationId, PendingContinuation continuation, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
