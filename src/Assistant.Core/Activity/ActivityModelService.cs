using System.Runtime.CompilerServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Activity;

/// <summary>
/// Reports the wait for another <see cref="IModelService"/> to the <see cref="IActivityTracker"/>: from asking it to
/// generate until its first chunk arrives, or until it ends, so loading the model and thinking show, and a response
/// that is already streaming does not. Neither the request nor the response is passed on.
/// </summary>
public sealed class ActivityModelService(IModelService inner, IActivityTracker tracker) : IModelService
{
    /// <inheritdoc/>
    public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
        inner.GetActiveModelAsync(cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var activity = tracker.Begin(ActivityKind.Model, cancellationToken: cancellationToken);
        try
        {
            // Cancelling the activity, or the caller's token, cancels the generation; once the activity has ended with
            // the first chunk, the caller's token still does.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, activity.CancellationToken);
            await foreach (var chunk in inner.GenerateAsync(request, linked.Token).WithCancellation(linked.Token)
                               .ConfigureAwait(false))
            {
                activity.Dispose();
                yield return chunk;
            }
        }
        finally
        {
            activity.Dispose();
        }
    }
}
