using Assistant.Core.Domain;
using Assistant.Core.Orchestration;

namespace Assistant.Core.Contracts;

/// <summary>
/// Runs one chat turn (PROJECT_SPEC §5.5): builds the model's prompt from the user's message, the context they scoped,
/// the system instructions and the conversation so far, streams the model's answer, and records both messages in the
/// in-memory conversation. Not to be confused with <see cref="IConversationService"/>, which stores conversations.
/// </summary>
public interface IAssistantOrchestrator
{
    /// <summary>
    /// Asks the model <paramref name="prompt"/> in <paramref name="session"/> and streams its answer as
    /// <see cref="AssistantResponseChunk"/>s, after any <see cref="AssistantResponseChunkType.Notice"/> that tells the
    /// user something was left out of the prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user's message, with its context items, is added to the session's conversation when the turn starts, and
    /// the Assistant's message grows with each text chunk, so the conversation always holds what has been said. A turn
    /// that is stopped or fails keeps what the model said before that; one that never got as far as the model (no model
    /// is set up) leaves the conversation as it was. A session runs one turn at a time.
    /// </para>
    /// <para>
    /// The prompt is fitted into the model's context window under the user's context limits
    /// (<see cref="Settings.ContextLimitSettings"/>, read as the turn starts): what does not fit is left out of the
    /// prompt, never out of the session's conversation, and each thing left out or cut short is reported in a
    /// <see cref="AssistantResponseChunkType.Notice"/> before the answer. The model is asked for no more than the tokens
    /// reserved for the answer, so a longer answer ends with a notice that it reached the length limit.
    /// </para>
    /// <para>
    /// Tool calls the model requests are passed on but not run: the tool loop is a later step.
    /// </para>
    /// </remarks>
    /// <param name="session">The conversation to ask in. Its messages before this one are the prior turns.</param>
    /// <param name="prompt">The text the user typed.</param>
    /// <param name="contextItems">The context the user scoped for this turn, or <see langword="null"/> for none.</param>
    /// <param name="instructions">
    /// System instructions for this turn, or <see langword="null"/> for <see cref="AssistantInstructions.Default"/>.
    /// The guidance on untrusted context is always added.
    /// </param>
    /// <param name="cancellationToken">Stops the turn; what the model said so far stays in the conversation.</param>
    /// <exception cref="ArgumentException"><paramref name="prompt"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The session is still answering an earlier message.</exception>
    /// <exception cref="ModelNotSetUpException">No local model is set up.</exception>
    /// <exception cref="ModelHosting.ModelHostException">The model could not answer.</exception>
    IAsyncEnumerable<AssistantResponseChunk> AskAsync(
        ConversationSession session,
        string prompt,
        IReadOnlyList<ContextItem>? contextItems = null,
        string? instructions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Goes back to a request that was set aside while the user decided whether to install an integration it needs (PROJECT_SPEC §4.8, step 110), so
    /// that the user never has to say it again. With <see cref="PendingOutcome.Installed"/> the request is answered as it would have been had the
    /// integration been there from the start: the offer's words leave what the model remembers of the conversation, the request goes through the
    /// same checks as a new one (a request the installed integration cannot serve is explained, not offered the same thing again) and then to the
    /// model, with only the few tools of connected apps that fit it, and its answer streams as <see cref="AskAsync"/>'s does. With
    /// <see cref="PendingOutcome.NotInstalled"/> nothing is done: the answer is the Assistant's own words that the integration was not installed and
    /// the request was not carried out, and the model is never asked. No message of the user's is added: the request is the one already in the conversation.
    /// </summary>
    /// <param name="session">The conversation the request was made in.</param>
    /// <param name="pending">The request, as the offer's reply handed it out.</param>
    /// <param name="outcome">What the user decided.</param>
    /// <param name="cancellationToken">Stops the turn; what was said so far stays in the conversation.</param>
    /// <exception cref="NotSupportedException">The orchestrator does not go back to requests.</exception>
    /// <exception cref="InvalidOperationException">The session is still answering an earlier message.</exception>
    /// <exception cref="ModelNotSetUpException">No local model is set up (<see cref="PendingOutcome.Installed"/> only).</exception>
    /// <exception cref="ModelHosting.ModelHostException">The model could not answer.</exception>
    IAsyncEnumerable<AssistantResponseChunk> ContinueAsync(
        ConversationSession session,
        PendingRequest pending,
        PendingOutcome outcome,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This orchestrator does not go back to a request that was set aside.");
}
