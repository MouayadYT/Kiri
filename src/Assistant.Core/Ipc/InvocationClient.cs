namespace Assistant.Core.Ipc;

/// <summary>The entry points' side of the app's pipe (<see cref="InvocationProtocol"/>): one request, one reply.</summary>
public static class InvocationClient
{
    /// <summary>Sends <paramref name="request"/> on <paramref name="connection"/>, a connected pipe to the app, and reads the reply.</summary>
    /// <exception cref="IpcProtocolException">The app closed the connection without replying, or replied with something else.</exception>
    /// <exception cref="IOException">The connection broke.</exception>
    public static async Task<InvocationReply> SendAsync(
        Stream connection, InvocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        await IpcFraming.WriteFrameAsync(connection, InvocationProtocol.EncodeRequest(request), cancellationToken)
            .ConfigureAwait(false);
        var reply = await IpcFraming.ReadFrameAsync(connection, InvocationProtocol.MaxFrameLength, cancellationToken)
            .ConfigureAwait(false);
        return reply is null
            ? throw new IpcProtocolException("The app closed the connection without replying.")
            : InvocationProtocol.DecodeReply(reply);
    }
}
