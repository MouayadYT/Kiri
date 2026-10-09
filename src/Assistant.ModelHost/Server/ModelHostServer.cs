using System.Diagnostics;
using System.IO.Pipes;
using Assistant.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Server;

/// <summary>
/// Creates the host's pipe, waits for its owner to connect, and serves that one connection.
/// </summary>
/// <remarks>
/// The pipe takes one connection, from the current user only, and must be the first pipe of its name, so no other
/// process can hold the name first. The host serves until the owner disconnects or asks it to shut down, and gives up
/// when the owner exits or does not connect within <see cref="ModelHostOptions.ConnectTimeout"/>.
/// </remarks>
internal sealed class ModelHostServer(
    ModelHostOptions options,
    ModelHostSession session,
    TimeProvider timeProvider,
    ILogger<ModelHostServer> logger)
{
    /// <summary>Serves the owner, and returns why serving ended.</summary>
    public async Task<ModelHostExitReason> RunAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = new NamedPipeServerStream(
                options.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                LocalPipe.Options | PipeOptions.FirstPipeInstance);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ModelHostLog.PipeUnavailable(logger, exception);
            return ModelHostExitReason.PipeUnavailable;
        }

        await using (pipe.ConfigureAwait(false))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ownerExit = OwnerProcess.WaitForExitAsync(options.OwnerProcessId, stop.Token);
            try
            {
                var start = Stopwatch.GetTimestamp();
                if (await WaitForOwnerAsync(pipe, ownerExit, stop.Token).ConfigureAwait(false) is { } reason)
                {
                    return reason;
                }

                ModelHostLog.OwnerConnected(logger, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                var serving = session.RunAsync(pipe, stop.Token);
                if (await Task.WhenAny(serving, ownerExit).ConfigureAwait(false) == ownerExit
                    && ownerExit.IsCompletedSuccessfully)
                {
                    stop.Cancel();
                    await serving.ConfigureAwait(false);
                    return ModelHostExitReason.OwnerExited;
                }

                return await serving.ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAny(ownerExit).ConfigureAwait(false);
            }
        }
    }

    // Returns null once the owner is connected, or why it never will be.
    private async Task<ModelHostExitReason?> WaitForOwnerAsync(
        NamedPipeServerStream pipe,
        Task ownerExit,
        CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connect = pipe.WaitForConnectionAsync(waiting.Token);
        var timeout = Task.Delay(options.ConnectTimeout, timeProvider, waiting.Token);
        await Task.WhenAny(connect, ownerExit, timeout).ConfigureAwait(false);
        waiting.Cancel();

        try
        {
            // The owner may have connected just as the wait ended; then serve it.
            await connect.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            return ModelHostExitReason.OwnerDisconnected;
        }

        if (ownerExit.IsCompletedSuccessfully)
        {
            return ModelHostExitReason.OwnerExited;
        }

        return timeout.IsCompletedSuccessfully ? ModelHostExitReason.ConnectTimedOut : ModelHostExitReason.Stopped;
    }
}
