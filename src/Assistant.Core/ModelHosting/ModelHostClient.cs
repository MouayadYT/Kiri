using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// The app's end of a model-host connection: sends requests and matches the host's replies to them by id.
/// </summary>
/// <remarks>
/// A background loop reads the host's frames from the moment the client is created until the connection closes, which
/// fails every request still waiting. It logs ids, counts and outcomes only (PROJECT_SPEC §3.3). The host's own
/// <see cref="ModelStatusReport"/>s, which answer no request, are raised as <see cref="ModelStatusReported"/> on that
/// loop, so a handler must return quickly.
/// </remarks>
public sealed partial class ModelHostClient : IAsyncDisposable
{
    private readonly ModelHostChannel _channel;
    private readonly ILogger<ModelHostClient> _logger;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<long, Generation> _generations = new();

    // The host runs one generation at a time. It is never disposed: a generation's end may release it after the client.
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _readLoop;
    private long _lastRequestId;
    private volatile bool _closed;
    private int _disposed;

    /// <summary>Starts a client on a stream already connected to a model host. The client owns the stream.</summary>
    public ModelHostClient(Stream connection, ILogger<ModelHostClient> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        _channel = new ModelHostChannel(connection);
        _logger = logger;
        _readLoop = Task.Run(() => ReadRepliesAsync(_stop.Token));
    }

    /// <summary>Completes when the connection has closed, from either side.</summary>
    public Task Completion => _readLoop;

    /// <summary>
    /// Raised when the host reports that the model's status changed, whether or not a request of this client caused it.
    /// It is raised on the client's read loop, in the order the host sent the reports.
    /// </summary>
    public event EventHandler<ModelStatusReport>? ModelStatusReported;

    /// <summary>
    /// Sends the ping, a <see cref="HealthRequest"/>, and waits for the host's <see cref="HealthReport"/>. It also
    /// proves that both sides speak <see cref="ModelHostProtocol.Version"/>.
    /// </summary>
    /// <exception cref="ModelHostException">
    /// The host answered with an error (its <see cref="ModelHostException.Code"/>), for example
    /// <see cref="ModelHostErrorCode.UnsupportedProtocolVersion"/>, or the connection closed.
    /// </exception>
    public Task<HealthReport> PingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<HealthReport>(new HealthRequest(), null, cancellationToken);

    /// <summary>
    /// Asks the host to load a model, replacing the one loaded, and waits until it is ready. The host also reports each
    /// step as a status (<see cref="ModelStatusReported"/>).
    /// </summary>
    /// <param name="request">The model and its files.</param>
    /// <param name="progress">Receives the load's progress from 0 to 1, when the host reports it.</param>
    /// <param name="cancellationToken">
    /// Stops waiting. The host keeps loading unless it is asked to load or unload another model.
    /// </param>
    /// <returns>The loaded model's identity and capabilities.</returns>
    /// <exception cref="ModelHostException">
    /// The host answered with an error (its <see cref="ModelHostException.Code"/>), such as
    /// <see cref="ModelHostErrorCode.ModelNotFound"/> or <see cref="ModelHostErrorCode.ModelLoadFailed"/>, or the
    /// connection closed.
    /// </exception>
    public async Task<ModelInfo> LoadModelAsync(
        LoadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await RequestAsync<ModelLoaded>(request, progress, cancellationToken).ConfigureAwait(false);
        return loaded.Model;
    }

    /// <summary>
    /// Asks the host to unload the model, ending a load in progress, and waits until its memory is freed. It succeeds
    /// also when no model was loaded.
    /// </summary>
    /// <returns>The identifier of the model that was unloaded, or <see langword="null"/> when none was loaded.</returns>
    /// <exception cref="ModelHostException">The host answered with an error, or the connection closed.</exception>
    public async Task<string?> UnloadModelAsync(CancellationToken cancellationToken = default)
    {
        var unloaded = await RequestAsync<ModelUnloaded>(new UnloadModelRequest(), null, cancellationToken)
            .ConfigureAwait(false);
        return unloaded.ModelId;
    }

    /// <summary>
    /// Asks the host to generate an answer and streams its replies as they arrive: <see cref="TextDelta"/>s and
    /// <see cref="ToolCallGenerated"/>s, in the order the host sent them, and last the <see cref="GenerationEnded"/>
    /// that says why it stopped.
    /// </summary>
    /// <remarks>
    /// The host runs one generation at a time, so a generation first waits until the one before it on this connection
    /// has ended, including one that was stopped and whose end is still on its way. Cancelling
    /// <paramref name="cancellationToken"/>, or leaving the stream before its end, asks the host to stop
    /// (<see cref="CancelGenerationRequest"/>); the replies still on their way are dropped.
    /// </remarks>
    /// <exception cref="ModelHostException">
    /// The host answered with an error (its <see cref="ModelHostException.Code"/>), such as
    /// <see cref="ModelHostErrorCode.ModelNotFound"/> or <see cref="ModelHostErrorCode.ContextExceeded"/>, perhaps after
    /// some replies, or the connection closed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async IAsyncEnumerable<ModelHostReply> GenerateAsync(
        GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // From here the gate is the generation's: it is released once the host has ended it, whoever ends it.
        var id = Interlocked.Increment(ref _lastRequestId);
        var generation = new Generation(_generationGate);
        _generations[id] = generation;
        var sent = false;
        var ended = false;
        try
        {
            // As in RequestAsync: a generation registered after the read loop has failed the others fails here.
            if (_closed)
            {
                throw Closed();
            }

            try
            {
                await _channel.SendAsync(id, request, cancellationToken).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                throw Closed(exception);
            }

            var replies = generation.Replies.Reader;
            while (await replies.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (replies.TryRead(out var reply))
                {
                    ended = reply is GenerationEnded;
                    yield return reply;
                    if (ended)
                    {
                        yield break;
                    }
                }
            }

            // The stream ended without its last reply: the host answered with an error, or the connection closed.
            ended = true;
            throw generation.Failure ?? Closed();
        }
        finally
        {
            if (!ended)
            {
                StopGeneration(id, generation, sent);
            }
        }
    }

    /// <summary>
    /// Asks the host to exit, and waits until it has stopped its work. The host closes the connection and exits next.
    /// </summary>
    /// <exception cref="ModelHostException">The host answered with an error, or the connection closed first.</exception>
    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<ShutdownAccepted>(new ShutdownRequest(), null, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _readLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task<TReply> RequestAsync<TReply>(
        ModelHostRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
        where TReply : ModelHostReply
    {
        var id = Interlocked.Increment(ref _lastRequestId);
        var reply = new TaskCompletionSource<ModelHostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingRequest(reply, progress);
        try
        {
            // The read loop marks the connection closed before it fails the waiting requests, so a request registered
            // after that is failed here instead.
            if (_closed)
            {
                throw Closed();
            }

            await _channel.SendAsync(id, request, cancellationToken).ConfigureAwait(false);
            var answer = await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return answer as TReply ?? throw new ModelHostException(ModelHostErrorCode.UnknownMessageType);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw Closed(exception);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // Ends a generation its caller left before its last reply. It never waits: a frame on a pipe is written only as the
    // host reads it, and the caller must not wait on the host to leave.
    private void StopGeneration(long id, Generation generation, bool sent)
    {
        if (!sent)
        {
            // Nothing reached the host, so nothing will come back.
            _generations.TryRemove(id, out _);
            generation.End();
            return;
        }

        // The host answers with the generation's end, which releases the gate; until then its replies are dropped.
        if (generation.Abandon())
        {
            _ = SendCancelAsync(id);
        }
    }

    private async Task SendCancelAsync(long generationId)
    {
        try
        {
            await _channel.SendAsync(Interlocked.Increment(ref _lastRequestId), new CancelGenerationRequest(generationId))
                .ConfigureAwait(false);
            LogGenerationCancelSent(_logger, generationId);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The connection is closing, which ends the generation too.
        }
    }

    private async Task ReadRepliesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                Deliver(frame);
            }

            LogHostClosedConnection(_logger);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed: the connection was closed on purpose.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LogConnectionBroken(_logger, exception);
        }
        catch (Exception exception)
        {
            LogReadLoopFailed(_logger, exception);
        }
        finally
        {
            _closed = true;
            foreach (var pending in _pending.Values)
            {
                pending.Reply.TrySetException(Closed());
            }

            foreach (var (id, generation) in _generations)
            {
                _generations.TryRemove(id, out _);
                generation.Fail(Closed());
            }
        }
    }

    private void Deliver(ModelHostFrame frame)
    {
        // A status report answers no request; the host sends it with id 0.
        if (frame is { Id: 0, Message: ModelStatusReport report })
        {
            RaiseStatusReported(report);
            return;
        }

        if (_generations.TryGetValue(frame.Id, out var generation))
        {
            DeliverToGeneration(frame, generation);
            return;
        }

        if (!_pending.TryGetValue(frame.Id, out var pending))
        {
            LogUnmatchedReply(_logger, frame.Id);
            return;
        }

        switch (frame.Message)
        {
            case ModelHostError error:
                pending.Reply.TrySetException(new ModelHostException(error.Code));
                break;
            case ModelLoadProgress loading:
                // Not the final reply: the load is still going.
                ReportProgress(pending, loading.Fraction);
                break;
            case ModelHostReply reply:
                pending.Reply.TrySetResult(reply);
                break;
            default:
                // A frame the client could not read, or a request sent the wrong way.
                LogUnreadableReply(_logger, frame.Id, frame.Error ?? ModelHostErrorCode.UnknownMessageType);
                pending.Reply.TrySetException(new ModelHostException(frame.Error ?? ModelHostErrorCode.UnknownMessageType));
                break;
        }
    }

    private void DeliverToGeneration(ModelHostFrame frame, Generation generation)
    {
        switch (frame.Message)
        {
            case TextDelta or ToolCallGenerated:
                generation.Add((ModelHostReply)frame.Message);
                return;
            case GenerationEnded end:
                _generations.TryRemove(frame.Id, out _);
                generation.End(end);
                return;
            case ModelHostError error:
                _generations.TryRemove(frame.Id, out _);
                LogGenerationFailed(_logger, frame.Id, error.Code);
                generation.Fail(new ModelHostException(error.Code));
                return;
            default:
                // A frame the client could not read, or a reply that does not belong to a generation.
                var code = frame.Error ?? ModelHostErrorCode.UnknownMessageType;
                _generations.TryRemove(frame.Id, out _);
                LogUnreadableReply(_logger, frame.Id, code);
                generation.Fail(new ModelHostException(code));
                return;
        }
    }

    private void RaiseStatusReported(ModelStatusReport report)
    {
        try
        {
            ModelStatusReported?.Invoke(this, report);
        }
        catch (Exception exception)
        {
            // A subscriber's failure must not end the loop that every reply arrives on.
            LogSubscriberFailed(_logger, exception);
        }
    }

    private void ReportProgress(PendingRequest pending, double fraction)
    {
        try
        {
            pending.Progress?.Report(fraction);
        }
        catch (Exception exception)
        {
            LogSubscriberFailed(_logger, exception);
        }
    }

    private static ModelHostException Closed(Exception? innerException = null) =>
        new("The connection to the model host is closed.", innerException);

    private sealed record PendingRequest(TaskCompletionSource<ModelHostReply> Reply, IProgress<double>? Progress);

    // One generation's replies, from the read loop to its reader. The read loop never waits on it.
    private sealed class Generation(SemaphoreSlim gate)
    {
        private readonly object _sync = new();
        private bool _abandoned;
        private bool _ended;

        public Channel<ModelHostReply> Replies { get; } = Channel.CreateUnbounded<ModelHostReply>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Why the generation ended without its last reply; read once the replies have completed.
        public ModelHostException? Failure { get; private set; }

        public void Add(ModelHostReply reply)
        {
            lock (_sync)
            {
                if (!_abandoned && !_ended)
                {
                    Replies.Writer.TryWrite(reply);
                }
            }
        }

        public void End(GenerationEnded? last = null)
        {
            lock (_sync)
            {
                if (last is not null && !_abandoned && !_ended)
                {
                    Replies.Writer.TryWrite(last);
                }

                Finish();
            }
        }

        public void Fail(ModelHostException failure)
        {
            lock (_sync)
            {
                if (!_ended)
                {
                    Failure = failure;
                }

                Finish();
            }
        }

        // Its reader has gone: later replies are dropped. Returns false when it has already ended.
        public bool Abandon()
        {
            lock (_sync)
            {
                _abandoned = true;
                return !_ended;
            }
        }

        // Must be called with _sync held. The gate is released once, when the host is done with the generation.
        private void Finish()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Replies.Writer.TryComplete();
            gate.Release();
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Debug, Message = "Model host closed the connection")]
    private static partial void LogHostClosedConnection(ILogger logger);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "Connection to the model host broke")]
    private static partial void LogConnectionBroken(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "Reading from the model host failed")]
    private static partial void LogReadLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Model host replied to request {RequestId}, which is not waiting")]
    private static partial void LogUnmatchedReply(ILogger logger, long requestId);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Model host's reply to request {RequestId} could not be read ({ErrorCode})")]
    private static partial void LogUnreadableReply(ILogger logger, long requestId, ModelHostErrorCode errorCode);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Error, Message = "A subscriber to the model host's updates failed")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Debug, Message = "Asked the model host to stop generation {RequestId}")]
    private static partial void LogGenerationCancelSent(ILogger logger, long requestId);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning, Message = "Model host ended generation {RequestId} with an error ({ErrorCode})")]
    private static partial void LogGenerationFailed(ILogger logger, long requestId, ModelHostErrorCode errorCode);
}
