using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Generation;

/// <summary>Answers the owner's generation requests with the loaded model.</summary>
internal interface ITextGenerator
{
    /// <summary>
    /// Generates the answer to <paramref name="request"/> and sends it as it comes: <see cref="TextDelta"/>s and
    /// <see cref="ToolCallGenerated"/>s, then <see cref="GenerationEnded"/>, or a <see cref="ModelHostError"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled: the owner stopped the generation, and the session sends its
    /// end.
    /// </exception>
    Task GenerateAsync(GenerationRequest request, IModelHostReplies replies, CancellationToken cancellationToken);
}

/// <summary>
/// Streams answers from the model engine (<see cref="IChatEngine"/>) to the owner, one generation at a time, since the
/// engine has one slot: a request that arrives while one runs is answered with <see cref="ModelHostErrorCode.Busy"/>.
/// </summary>
/// <remarks>
/// It generates only with the model the <see cref="IModelController"/> has loaded, and answers a request for any other
/// with <see cref="ModelHostErrorCode.ModelNotFound"/>. A request with images needs a model loaded with its multimodal
/// projector, and is otherwise answered with <see cref="ModelHostErrorCode.VisionNotSupported"/>. The log holds counts,
/// reasons and timings only, never text or images.
/// </remarks>
internal sealed class TextGenerator(
    IModelController models,
    IChatEngine engine,
    TimeProvider timeProvider,
    ILogger<TextGenerator> logger) : ITextGenerator
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task GenerateAsync(GenerationRequest request, IModelHostReplies replies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replies);

        if (Refuse(request) is { } refusal)
        {
            GenerationLog.Refused(logger, refusal);
            await replies.SendAsync(new ModelHostError(refusal), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await StreamAsync(request, replies, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    // Why the request cannot run now, or null once it holds the one slot.
    private ModelHostErrorCode? Refuse(GenerationRequest request)
    {
        if (models.Model is not { } model || model.Id != request.ModelId)
        {
            return ModelHostErrorCode.ModelNotFound;
        }

        if (request is GenerateMultimodalRequest && !model.SupportsVision)
        {
            return ModelHostErrorCode.VisionNotSupported;
        }

        return _running.Wait(0) ? null : ModelHostErrorCode.Busy;
    }

    private async Task StreamAsync(GenerationRequest request, IModelHostReplies replies, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetTimestamp();
        long? firstTextMs = null;
        var pieces = 0;
        var toolCalls = 0;
        try
        {
            await foreach (var step in engine.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                switch (step)
                {
                    case ChatTextEvent text:
                        firstTextMs ??= ElapsedMs(start);
                        pieces++;
                        await replies.SendAsync(new TextDelta(text.Text), cancellationToken).ConfigureAwait(false);
                        break;
                    case ChatToolCallEvent call:
                        toolCalls++;
                        await replies.SendAsync(new ToolCallGenerated(call.Call), cancellationToken).ConfigureAwait(false);
                        break;
                    case ChatFinishedEvent finished:
                        GenerationLog.Completed(
                            logger, finished.Reason, pieces, toolCalls, finished.PromptTokens, finished.OutputTokens,
                            firstTextMs, ElapsedMs(start));
                        await replies.SendAsync(
                            new GenerationEnded(finished.Reason)
                            {
                                PromptTokens = finished.PromptTokens,
                                OutputTokens = finished.OutputTokens,
                            },
                            cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // However the engine's connection ended, the owner stopped it; the session sends the end.
            GenerationLog.Stopped(logger, pieces, ElapsedMs(start));
            throw new OperationCanceledException(cancellationToken);
        }
        catch (GenerationException failed)
        {
            GenerationLog.Failed(logger, failed.Code, pieces, ElapsedMs(start));
            await replies.SendAsync(new ModelHostError(failed.Code), cancellationToken).ConfigureAwait(false);
            return;
        }

        // The engine's stream always ends with its finish; one that does not has broken off.
        GenerationLog.Failed(logger, ModelHostErrorCode.GenerationFailed, pieces, ElapsedMs(start));
        await replies.SendAsync(new ModelHostError(ModelHostErrorCode.GenerationFailed), cancellationToken).ConfigureAwait(false);
    }

    private long ElapsedMs(long start) => (long)timeProvider.GetElapsedTime(start).TotalMilliseconds;
}
