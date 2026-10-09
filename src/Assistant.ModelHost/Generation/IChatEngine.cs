using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Generation;

/// <summary>Streams an answer from the running model engine.</summary>
internal interface IChatEngine
{
    /// <summary>
    /// Asks the engine to answer <paramref name="request"/> with the model it has loaded, and streams its answer as it
    /// comes. It ends with one <see cref="ChatFinishedEvent"/>. Leaving the stream early, or cancelling it, stops the
    /// engine's work.
    /// </summary>
    /// <exception cref="GenerationException">The engine refused the request, reported an error, or broke off.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    IAsyncEnumerable<ChatCompletionEvent> StreamAsync(GenerationRequest request, CancellationToken cancellationToken);
}
