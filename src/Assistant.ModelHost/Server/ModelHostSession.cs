using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Models;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Server;

/// <summary>
/// Serves the owner's one connection: reads its requests, runs each concurrently through the
/// <see cref="IModelHostRequestHandler"/>, and keeps the protocol's promise that every request but a cancel gets
/// exactly one final reply (<see cref="ModelHostProtocol"/>).
/// </summary>
/// <remarks>
/// Frames that cannot be read are answered with a <see cref="ModelHostError"/>. A <see cref="CancelGenerationRequest"/>
/// cancels the generation it names. A <see cref="ShutdownRequest"/> stops reading, stops every running request, and
/// is answered with <see cref="ShutdownAccepted"/> once they have ended. While it serves, every change of the model's
/// status is sent to the owner as a <see cref="ModelStatusReport"/> with id 0, in order. Logs carry request types, ids,
/// durations and outcomes only.
/// </remarks>
internal sealed class ModelHostSession(
    IModelHostRequestHandler handler,
    ILogger<ModelHostSession> logger,
    IModelStatusSource? statusSource = null)
{
    /// <summary>Serves <paramref name="connection"/> until the owner leaves or asks the host to shut down.</summary>
    /// <param name="connection">The connected pipe, which the session closes when it ends.</param>
    /// <param name="cancellationToken">Stops the session, as when the host itself is stopped.</param>
    public async Task<ModelHostExitReason> RunAsync(Stream connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var channel = new ModelHostChannel(connection);
        await using (channel.ConfigureAwait(false))
        {
            var running = new ConcurrentDictionary<long, RunningRequest>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var statuses = StatusFeed.Start(statusSource, channel);
            (ModelHostExitReason Reason, long ShutdownId) outcome;
            try
            {
                outcome = await ReadRequestsAsync(channel, running, statuses, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                // Every running request ends, sending its final reply while the connection is still open, and the
                // statuses they changed are sent before the connection closes.
                stop.Cancel();
                await Task.WhenAll(running.Values.Select(request => request.Completion)).ConfigureAwait(false);
                await statuses.StopAsync().ConfigureAwait(false);
            }

            if (outcome.Reason == ModelHostExitReason.ShutdownRequested)
            {
                await TrySendAsync(channel, outcome.ShutdownId, new ShutdownAccepted()).ConfigureAwait(false);
            }

            return outcome.Reason;
        }
    }

    private async Task<(ModelHostExitReason Reason, long ShutdownId)> ReadRequestsAsync(
        ModelHostChannel channel,
        ConcurrentDictionary<long, RunningRequest> running,
        StatusFeed statuses,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ModelHostFrame? frame;
            try
            {
                frame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (ModelHostExitReason.Stopped, 0);
            }
            catch (IpcProtocolException exception)
            {
                ModelHostLog.FramingBroken(logger, exception);
                return (ModelHostExitReason.ProtocolViolation, 0);
            }
            catch (IOException)
            {
                return (ModelHostExitReason.OwnerDisconnected, 0);
            }

            if (frame is null)
            {
                return (ModelHostExitReason.OwnerDisconnected, 0);
            }

            var rejection = frame.Message switch
            {
                null => frame.Error,
                ShutdownRequest => null,
                CancelGenerationRequest => null,
                ModelHostRequest when frame.Id <= 0 || running.ContainsKey(frame.Id) => ModelHostErrorCode.MalformedMessage,
                ModelHostRequest => null,

                // A reply: only the host sends those.
                _ => ModelHostErrorCode.UnknownMessageType,
            };

            if (rejection is { } error)
            {
                ModelHostLog.FrameRejected(logger, frame.Id, error);
                if (!await TrySendAsync(channel, frame.Id, new ModelHostError(error)).ConfigureAwait(false))
                {
                    return (ModelHostExitReason.OwnerDisconnected, 0);
                }

                continue;
            }

            switch (frame.Message)
            {
                case ShutdownRequest:
                    ModelHostLog.ShutdownRequested(logger, running.Count);
                    return (ModelHostExitReason.ShutdownRequested, frame.Id);

                case CancelGenerationRequest cancel:
                    if (running.TryGetValue(cancel.RequestId, out var target) && target.Request is GenerationRequest)
                    {
                        target.Cancel();
                    }

                    break;

                case ModelHostRequest request:
                    Start(channel, running, statuses, frame.Id, request, cancellationToken);
                    break;
            }
        }
    }

    private void Start(
        ModelHostChannel channel,
        ConcurrentDictionary<long, RunningRequest> running,
        StatusFeed statuses,
        long id,
        ModelHostRequest request,
        CancellationToken sessionToken)
    {
        var entry = new RunningRequest(request, CancellationTokenSource.CreateLinkedTokenSource(sessionToken));
        running[id] = entry;
        entry.Completion = RunRequestAsync(channel, running, statuses, id, entry);
    }

    private async Task RunRequestAsync(
        ModelHostChannel channel,
        ConcurrentDictionary<long, RunningRequest> running,
        StatusFeed statuses,
        long id,
        RunningRequest entry)
    {
        // The read loop goes straight back to the pipe, so a cancel can overtake the request it names.
        await Task.Yield();

        var start = Stopwatch.GetTimestamp();
        var request = entry.Request;
        var cancellationToken = entry.Cancellation.Token;
        try
        {
            await handler
                .HandleAsync(request, new Replies(channel, statuses, id), cancellationToken)
                .ConfigureAwait(false);
            ModelHostLog.RequestHandled(logger, request.GetType(), id, ElapsedMs(start));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ModelHostLog.RequestCancelled(logger, request.GetType(), id, ElapsedMs(start));
            ModelHostReply final = request is GenerationRequest
                ? new GenerationEnded(GenerationStopReason.Cancelled)
                : new ModelHostError(ModelHostErrorCode.ShuttingDown);
            await TrySendAsync(channel, id, final).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The connection closed while the request ran: nobody is left to answer.
        }
        catch (Exception exception)
        {
            ModelHostLog.RequestFailed(logger, request.GetType(), id, exception);
            await TrySendAsync(channel, id, new ModelHostError(ModelHostErrorCode.Internal)).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(id, out _);
            entry.Cancellation.Dispose();
        }
    }

    private static async Task<bool> TrySendAsync(ModelHostChannel channel, long id, ModelHostReply reply)
    {
        try
        {
            await channel.SendAsync(id, reply).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private sealed class RunningRequest(ModelHostRequest request, CancellationTokenSource cancellation)
    {
        public ModelHostRequest Request { get; } = request;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended while the cancel was on its way.
            }
        }
    }

    // Sends the model's status changes to the owner, one at a time in the order they happened.
    private sealed class StatusFeed
    {
        private readonly IModelStatusSource? _source;
        private readonly Channel<(ModelStatusReport? Report, TaskCompletionSource? Flushed)> _queue =
            Channel.CreateUnbounded<(ModelStatusReport? Report, TaskCompletionSource? Flushed)>(
                new UnboundedChannelOptions { SingleReader = true });
        private Task _sending = Task.CompletedTask;

        private StatusFeed(IModelStatusSource? source) => _source = source;

        public static StatusFeed Start(IModelStatusSource? source, ModelHostChannel channel)
        {
            var feed = new StatusFeed(source);
            if (source is not null)
            {
                source.Changed += feed.OnChanged;
                feed._sending = Task.Run(() => feed.SendAsync(channel));
            }

            return feed;
        }

        // Waits until every status queued so far has been sent, so a reply never overtakes the change it followed.
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_source is null)
            {
                return;
            }

            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_queue.Writer.TryWrite((null, flushed)))
            {
                await flushed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            if (_source is not null)
            {
                _source.Changed -= OnChanged;
            }

            _queue.Writer.TryComplete();
            await _sending.ConfigureAwait(false);
        }

        // Called with the source's lock held, so it only queues.
        private void OnChanged(object? sender, ModelStatusReport report) => _queue.Writer.TryWrite((report, null));

        // Reads to the end even when the connection is broken, so a flush never waits for a sender that has given up.
        private async Task SendAsync(ModelHostChannel channel)
        {
            var open = true;
            await foreach (var (report, flushed) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (report is not null && open)
                {
                    open = await TrySendAsync(channel, 0, report).ConfigureAwait(false);
                }

                flushed?.TrySetResult();
            }
        }
    }

    private sealed class Replies(ModelHostChannel channel, StatusFeed statuses, long id) : IModelHostReplies
    {
        public async ValueTask SendAsync(ModelHostReply reply, CancellationToken cancellationToken = default)
        {
            await statuses.FlushAsync(cancellationToken).ConfigureAwait(false);
            await channel.SendAsync(id, reply, cancellationToken).ConfigureAwait(false);
        }
    }
}
