using Assistant.Core.Domain;

namespace Assistant.UI.Messages;

/// <summary>
/// What a part of an answer asks the conversation to do next, once the user has decided something in it (PROJECT_SPEC §4.8, step 110): the request that was set
/// aside while the user chose whether to install an integration, and what they chose. The conversation hands it to the provider that made the answer, which
/// goes back to the request (<see cref="Assistant.UI.ViewModels.IAnswerProvider.StreamContinuationAsync"/>), so the user never has to say it again.
/// </summary>
/// <param name="Pending">The request, as the answer's offer carried it.</param>
/// <param name="Outcome">Whether the integration it needed was installed.</param>
public sealed record PendingContinuation(PendingRequest Pending, PendingOutcome Outcome);

/// <summary>
/// A part of an answer that, once the user has acted on it, asks the conversation to go on with something that was set aside (step 110): the approval panel
/// of an integration, when the offer was made for a request. The conversation that shows the answer listens for it and runs the continuation as the next answer, under
/// the same Stop button, Searching chip and recording as any answer.
/// </summary>
public interface IContinuingContent
{
    /// <summary>Raised on the user interface's thread, once, when the user has decided and the request can go on.</summary>
    event EventHandler<PendingContinuation>? ContinuationRequested;
}
