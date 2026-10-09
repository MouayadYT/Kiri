using System.IO.Pipes;

namespace Assistant.Core.Ipc;

/// <summary>How forwarding a request to the app ended.</summary>
public enum ForwardResult
{
    /// <summary>The app took the request.</summary>
    Forwarded = 0,

    /// <summary>The app was not running and could not be started: it is not where this entry point expects it.</summary>
    AppNotFound = 1,

    /// <summary>The app did not answer in time, or the connection broke.</summary>
    AppDidNotRespond = 2,

    /// <summary>The app answered and refused the request, or answered with something this build does not understand.</summary>
    Refused = 3,
}

/// <summary>How long the forwarder waits, and the name of the lock that lets only one of its processes start the app.</summary>
public sealed record ForwarderOptions
{
    /// <summary>
    /// The lock taken while the app is being started, for this Windows session. Every entry point uses the same one by default, so
    /// a menu choice in File Explorer and one in a browser made together start the app once.
    /// </summary>
    public string StartLockName { get; init; } = @"Local\Assistant.App.Start";

    /// <summary>How long a connection to a running app may take, its pipe being busy with another file's request.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the app has to open its pipe after it was started, and how long another process waits to start it.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a running app that has no pipe yet (it is starting) has to open it.</summary>
    public TimeSpan StartingTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the app has to answer a request.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Hands a request to the running app over its pipe (PROJECT_SPEC §4.4, §5.7), starting the app first when it is not running.
/// File Explorer starts one of these processes for each selected file at about the same time, so only one of them starts the app
/// (<see cref="ForwarderOptions.StartLockName"/>), and the others wait for it and then connect.
/// </summary>
/// <param name="pipeName">The app's pipe (<see cref="AppPipe.ForCurrentUser"/>).</param>
/// <param name="starter">Finds out whether the app runs and starts it.</param>
/// <param name="options">How long to wait.</param>
/// <param name="connected">
/// Called with each connection to the app before the request is sent: an entry point that was started by the user's own action uses it
/// to give the app the right to bring its window to the front, which only a Windows-specific caller can.
/// </param>
public sealed class InvocationForwarder(
    string pipeName, IAppStarter starter, ForwarderOptions options, Action<NamedPipeClientStream>? connected = null)
{
    /// <summary>Forwards <paramref name="request"/>. It blocks until the app has answered or the time allowed has passed.</summary>
    public ForwardResult Forward(InvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var pipe = Connect(out var failure);
        if (pipe is null)
        {
            return failure;
        }

        connected?.Invoke(pipe);

        using var timeout = new CancellationTokenSource(options.ReplyTimeout);
        try
        {
            var reply = InvocationClient.SendAsync(pipe, request, timeout.Token).GetAwaiter().GetResult();
            return reply.IsAccepted ? ForwardResult.Forwarded : ForwardResult.Refused;
        }
        catch (IpcProtocolException)
        {
            return ForwardResult.Refused;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return ForwardResult.AppDidNotRespond;
        }
    }

    private NamedPipeClientStream? Connect(out ForwardResult failure)
    {
        failure = ForwardResult.AppDidNotRespond;
        if (TryConnect(options.ConnectTimeout) is { } running)
        {
            return running;
        }

        using var startLock = new Mutex(initiallyOwned: false, options.StartLockName);
        var owned = false;
        try
        {
            try
            {
                owned = startLock.WaitOne(options.StartTimeout);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            // Whoever held the lock may have started the app meanwhile.
            if (TryConnect(options.ConnectTimeout) is { } started)
            {
                return started;
            }

            if (!owned)
            {
                return null;
            }

            var wait = options.StartingTimeout;
            if (!starter.IsRunning())
            {
                if (!starter.Start())
                {
                    failure = ForwardResult.AppNotFound;
                    return null;
                }

                wait = options.StartTimeout;
            }

            return TryConnect(wait);
        }
        finally
        {
            if (owned)
            {
                startLock.ReleaseMutex();
            }
        }
    }

    // Only a pipe served by the current user is connected to.
    private NamedPipeClientStream? TryConnect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }
}
