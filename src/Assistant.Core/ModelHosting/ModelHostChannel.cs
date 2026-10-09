using Assistant.Core.Ipc;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Sends and receives model-host messages over a connected stream, one frame each. Both ends of the protocol use it:
/// the app's <see cref="ModelHostClient"/> and the host.
/// </summary>
/// <remarks>
/// Any number of callers may send at once; their frames never interleave. One caller receives at a time. Disposing
/// the channel closes the stream, which ends a pending receive.
/// </remarks>
public sealed class ModelHostChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Creates a channel over a connected stream, which the channel then owns.</summary>
    public ModelHostChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>Sends one message.</summary>
    /// <param name="id">The request id the message carries.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">
    /// Stops waiting for other senders. Once a frame is being written it is written whole, because half a frame would
    /// break the connection.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The message is larger than <see cref="ModelHostProtocol.MaxFrameLength"/>.
    /// </exception>
    /// <exception cref="IOException">The connection is broken.</exception>
    public async ValueTask SendAsync(long id, ModelHostMessage message, CancellationToken cancellationToken = default)
    {
        var payload = ModelHostSerializer.Serialize(id, message);
        if (payload.Length > ModelHostProtocol.MaxFrameLength)
        {
            throw new ArgumentException("The message is larger than the model-host protocol allows.", nameof(message));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcFraming.WriteFrameAsync(_stream, payload, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Receives the next frame.</summary>
    /// <returns>The frame, or <see langword="null"/> when the peer closed the connection.</returns>
    /// <exception cref="IpcProtocolException">The peer broke the framing; the connection cannot continue.</exception>
    /// <exception cref="IOException">The connection is broken.</exception>
    public async ValueTask<ModelHostFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await IpcFraming
            .ReadFrameAsync(_stream, ModelHostProtocol.MaxFrameLength, cancellationToken)
            .ConfigureAwait(false);
        return payload is null ? null : ModelHostSerializer.Deserialize(payload);
    }

    /// <inheritdoc/>
    /// <remarks>The write lock is left to the collector: a sender may still be releasing it.</remarks>
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
