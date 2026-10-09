using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Ipc;

/// <summary>How <see cref="InvocationServer.RunAsync"/> ended.</summary>
public enum InvocationServerExit
{
    /// <summary>It was stopped.</summary>
    Stopped = 0,

    /// <summary>
    /// Another process already serves the pipe's name, such as another copy of the app, so this one serves nothing.
    /// </summary>
    PipeUnavailable = 1,
}

/// <summary>
/// The running app's end of its pipe (<see cref="AppPipe"/>, PROJECT_SPEC §5.7): waits for the entry points, reads one request
/// from each connection, checks it, hands it to an <see cref="IInvocationHandler"/> and replies. Only the current user can
/// connect, the first instance of the name must be this server's, a frame is capped at
/// <see cref="InvocationProtocol.MaxFrameLength"/>, and a connection that does not send its request and read the reply
/// within the time allowed is closed. Requests are logged by outcome and count only, never by path.
/// </summary>
public sealed class InvocationServer
{
    /// <summary>How many connections are served at once; a further one waits for a free instance.</summary>
    public const int MaxConnections = 8;

    /// <summary>How long a connection has to send its request and read the reply, by default.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _pipeName;
    private readonly IInvocationHandler _handler;
    private readonly ILogger<InvocationServer> _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    /// <summary>Creates a server for the pipe <paramref name="pipeName"/>.</summary>
    public InvocationServer(
        string pipeName, IInvocationHandler handler, ILogger<InvocationServer> logger, TimeSpan? requestTimeout = null)
    {
        if (!LocalPipe.IsValidName(pipeName))
        {
            throw new ArgumentException("The pipe name must be a plain pipe name.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// Serves connections until <paramref name="cancellationToken"/> is cancelled, then waits for the ones being served to end.
    /// </summary>
    public async Task<InvocationServerExit> RunAsync(CancellationToken cancellationToken)
    {
        var first = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NamedPipeServerStream pipe;
                try
                {
                    pipe = Create(first);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (first)
                    {
                        InvocationLog.PipeUnavailable(_logger, exception);
                        return InvocationServerExit.PipeUnavailable;
                    }

                    // Every instance is serving a connection: wait for one to end.
                    await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (first)
                {
                    InvocationLog.Listening(_logger);
                    first = false;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException)
                {
                    // Stopped, or a client that gave up while it was being connected.
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    if (exception is OperationCanceledException)
                    {
                        throw;
                    }

                    continue;
                }

                Track(ServeAsync(pipe, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return InvocationServerExit.Stopped;
        }
        finally
        {
            await Task.WhenAll(_connections.Keys).ConfigureAwait(false);
        }
    }

    // The first instance must be the first of its name, so no other process can hold the name first and take the requests.
    private NamedPipeServerStream Create(bool first) => new(
        _pipeName,
        PipeDirection.InOut,
        MaxConnections,
        PipeTransmissionMode.Byte,
        LocalPipe.Options | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private void Track(Task connection)
    {
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(
            done => _connections.TryRemove(done, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                var payload = await IpcFraming.ReadFrameAsync(pipe, InvocationProtocol.MaxFrameLength, timeout.Token)
                    .ConfigureAwait(false);
                if (payload is null)
                {
                    return;
                }

                var reply = Answer(payload);
                await IpcFraming.WriteFrameAsync(pipe, InvocationProtocol.EncodeReply(reply), timeout.Token)
                    .ConfigureAwait(false);

                // The client closes its end once it has read the reply; closing ours first could throw the reply away.
                await pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                InvocationLog.ConnectionTimedOut(_logger);
            }
            catch (IOException exception)
            {
                InvocationLog.ConnectionBroken(_logger, exception);
            }
        }
    }

    private InvocationReply Answer(byte[] payload)
    {
        if (!InvocationProtocol.TryDecodeRequest(payload, out var request, out var error))
        {
            InvocationLog.RequestRejected(_logger, error);
            return new InvocationReply(error);
        }

        try
        {
            var reply = _handler.Handle(request!);
            InvocationLog.RequestHandled(_logger, request!.Action, request.Paths.Count, reply.Error);
            return reply;
        }
        catch (Exception exception)
        {
            InvocationLog.HandlerFailed(_logger, request!.Action, exception);
            return new InvocationReply(InvocationErrorCode.Unavailable);
        }
    }
}
