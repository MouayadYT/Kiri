using System.Diagnostics;
using System.Runtime.CompilerServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Diagnostics;

/// <summary>
/// Logs model-host failures and outcomes around another <see cref="IModelService"/>: counts, durations and
/// exception details only, never prompts or output.
/// </summary>
public sealed partial class LoggingModelService(IModelService inner, ILogger<LoggingModelService> logger)
    : IModelService
{
    /// <inheritdoc/>
    public async Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await inner.GetActiveModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                LogNoModel(logger);
            }

            return model;
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            LogModelQueryFailed(logger, exception);
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var chunks = 0;

        IAsyncEnumerator<AssistantResponseChunk> enumerator;
        try
        {
            enumerator = inner.GenerateAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            LogGenerationFailed(logger, chunks, ElapsedMs(start), exception);
            throw;
        }

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (Exception exception) when (!IsFailure(exception, cancellationToken))
                {
                    LogGenerationCancelled(logger, chunks, ElapsedMs(start));
                    throw;
                }
                catch (Exception exception)
                {
                    LogGenerationFailed(logger, chunks, ElapsedMs(start), exception);
                    throw;
                }

                chunks++;
                yield return enumerator.Current;
            }
        }

        LogGenerationCompleted(logger, chunks, ElapsedMs(start));
    }

    // Cancellation the caller asked for is an outcome, not a failure.
    private static bool IsFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private static long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "No usable model is installed")]
    private static partial void LogNoModel(ILogger logger);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Model host failed to report the active model")]
    private static partial void LogModelQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug, Message = "Generation completed: {ChunkCount} chunks in {ElapsedMs} ms")]
    private static partial void LogGenerationCompleted(ILogger logger, int chunkCount, long elapsedMs);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Generation cancelled after {ChunkCount} chunks in {ElapsedMs} ms")]
    private static partial void LogGenerationCancelled(ILogger logger, int chunkCount, long elapsedMs);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Error, Message = "Model host failed during generation after {ChunkCount} chunks in {ElapsedMs} ms")]
    private static partial void LogGenerationFailed(ILogger logger, int chunkCount, long elapsedMs, Exception exception);
}
