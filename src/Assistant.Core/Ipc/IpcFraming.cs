using System.Buffers.Binary;

namespace Assistant.Core.Ipc;

/// <summary>
/// Frames messages on a byte stream such as a named pipe (PROJECT_SPEC §5.7): each frame is a 4-byte little-endian
/// payload length followed by that many payload bytes.
/// </summary>
/// <remarks>
/// A frame never has an empty payload. The reader rejects a length above its limit before allocating anything, so a
/// broken or hostile peer cannot make it reserve memory. After a framing error the stream can no longer be read in
/// step, so the connection must be closed.
/// </remarks>
public static class IpcFraming
{
    /// <summary>Bytes in the length prefix.</summary>
    public const int HeaderLength = sizeof(int);

    /// <summary>Writes one frame and flushes the stream.</summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is empty.</exception>
    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A frame cannot be empty.", nameof(payload));
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame.</summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="maxPayloadLength">The largest payload accepted, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The payload, or <see langword="null"/> when the peer closed the stream cleanly between frames.
    /// </returns>
    /// <exception cref="IpcProtocolException">
    /// The length is zero, negative or above <paramref name="maxPayloadLength"/>, or the stream ended inside a frame.
    /// </exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream,
        int maxPayloadLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadLength);

        var header = new byte[HeaderLength];
        var headerRead = await stream
            .ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderLength)
        {
            throw new IpcProtocolException("The stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxPayloadLength)
        {
            throw new IpcProtocolException("A frame's length is outside the accepted range.");
        }

        var payload = new byte[length];
        var payloadRead = await stream
            .ReadAtLeastAsync(payload, length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new IpcProtocolException("The stream ended inside a frame.");
        }

        return payload;
    }
}
