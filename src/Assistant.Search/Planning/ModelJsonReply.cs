using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Search.Planning;

/// <summary>Why the model gave no JSON object to read.</summary>
internal enum ModelReplyFailure
{
    /// <summary>It did.</summary>
    None = 0,

    /// <summary>No local model is set up.</summary>
    NoModel = 1,

    /// <summary>The model could not be run, or failed while answering.</summary>
    ModelFailed = 2,

    /// <summary>The model did not answer in time.</summary>
    TimedOut = 3,

    /// <summary>The model's reply holds no JSON object.</summary>
    InvalidReply = 4,
}

/// <summary>The JSON object the model answered with, or why there is none. Nothing in it has been checked yet.</summary>
internal readonly record struct ModelJsonReply(string? Json, ModelReplyFailure Failure);

/// <summary>
/// Asks the local model a question that is answered with one short JSON object, and reads it as it streams in: as soon as the
/// object closes the model is stopped, so one that goes on talking costs no time. Nothing the model does can throw out of here but
/// the caller's own cancellation: a model that is missing, fails, runs out of time or answers without an object is a
/// <see cref="ModelReplyFailure"/>. What the object holds is for the caller to check, field by field, before it is used.
/// </summary>
internal static class ModelJsonAsker
{
    /// <summary>Puts <paramref name="request"/> to <paramref name="models"/> and gives the model <paramref name="timeout"/> to answer.</summary>
    public static async Task<ModelJsonReply> AskAsync(
        IModelService models,
        ModelRequest request,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var reader = new PlanReplyReader();
        var stoppedByUs = false;
        try
        {
            if (await models.GetActiveModelAsync(limit.Token).ConfigureAwait(false) is null)
            {
                return new ModelJsonReply(null, ModelReplyFailure.NoModel);
            }

            try
            {
                await foreach (var chunk in models.GenerateAsync(request, limit.Token).WithCancellation(limit.Token).ConfigureAwait(false))
                {
                    if (chunk is { Type: AssistantResponseChunkType.TextDelta, Text: { } piece })
                    {
                        reader.Append(piece);
                        if (reader.IsDone)
                        {
                            // The object is complete: whatever else the model would say is not wanted, and it is stopped.
                            stoppedByUs = true;
                            limit.Cancel();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppedByUs)
            {
                // Stopping the model as it is told to ends its stream with a cancellation; the reply is whole.
            }
        }
        catch (OperationCanceledException)
        {
            // The caller's own cancellation goes on; running out of time is a failure like any other.
            cancellationToken.ThrowIfCancellationRequested();
            return new ModelJsonReply(null, ModelReplyFailure.TimedOut);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The model could not be loaded or run, or failed while answering.
            PlanningLog.ModelFailed(logger, exception.GetType().Name);
            return new ModelJsonReply(null, ModelReplyFailure.ModelFailed);
        }

        return reader.Object is { } json
            ? new ModelJsonReply(json, ModelReplyFailure.None)
            : new ModelJsonReply(null, ModelReplyFailure.InvalidReply);
    }
}
