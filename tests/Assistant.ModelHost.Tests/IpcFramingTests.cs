using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.ModelHost.Tests;

public sealed class IpcFramingTests
{
    [Fact]
    public async Task Frames_RoundTrip_InOrder()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, "first"u8.ToArray());
        await IpcFraming.WriteFrameAsync(stream, new byte[70_000]);
        await IpcFraming.WriteFrameAsync(stream, "third"u8.ToArray());
        stream.Position = 0;

        Assert.Equal("first"u8.ToArray(), await IpcFraming.ReadFrameAsync(stream, 100_000));
        Assert.Equal(70_000, (await IpcFraming.ReadFrameAsync(stream, 100_000))!.Length);
        Assert.Equal("third"u8.ToArray(), await IpcFraming.ReadFrameAsync(stream, 100_000));
        Assert.Null(await IpcFraming.ReadFrameAsync(stream, 100_000));
    }

    [Fact]
    public async Task Header_IsLittleEndianLength()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, new byte[0x0102]);

        Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0x00 }, stream.ToArray()[..4]);
        Assert.Equal(4 + 0x0102, stream.Length);
    }

    [Fact]
    public async Task Frame_SplitAcrossTinyReads_IsReadWhole()
    {
        using var buffer = new MemoryStream();
        await IpcFraming.WriteFrameAsync(buffer, "hello, host"u8.ToArray());
        using var stream = new OneByteAtATimeStream(buffer.ToArray());

        Assert.Equal("hello, host"u8.ToArray(), await IpcFraming.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task EmptyStream_IsCleanEnd()
    {
        using var stream = new MemoryStream();

        Assert.Null(await IpcFraming.ReadFrameAsync(stream, 1024));
    }

    [Theory]
    [InlineData(new byte[] { 5 })]
    [InlineData(new byte[] { 5, 0, 0 })]
    [InlineData(new byte[] { 5, 0, 0, 0, (byte)'a', (byte)'b' })]
    public async Task StreamEndingInsideAFrame_IsRejected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadFrameAsync(stream, 1024).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    [InlineData(int.MaxValue)]
    public async Task LengthOutsideTheLimit_IsRejectedBeforeReadingThePayload(int length)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes, length);
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadFrameAsync(stream, 1024).AsTask());
        Assert.Equal(IpcFraming.HeaderLength, stream.Position);
    }

    [Fact]
    public async Task EmptyFrame_CannotBeWritten()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(
            () => IpcFraming.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty).AsTask());
        Assert.Equal(0, stream.Length);
    }

    private sealed class OneByteAtATimeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
