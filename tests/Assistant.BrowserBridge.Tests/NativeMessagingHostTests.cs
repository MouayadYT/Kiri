using System.Text;
using Assistant.BrowserBridge.Host;
using Assistant.BrowserBridge.Protocol;
using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.BrowserBridge.Tests;

/// <summary>
/// The native-messaging host's message loop (PROJECT_SPEC §4.5, §5.7), over in-memory streams: what it reads from the extension, what it
/// hands to the app, and what it answers. The real app is never involved.
/// </summary>
public sealed class NativeMessagingHostTests
{
    private static readonly BrowserSelection Sample = new("Some selected words", false, "The page", "https://example.test/page");

    private static byte[] Frame(byte[] payload)
    {
        using var buffer = new MemoryStream();
        IpcFraming.WriteFrameAsync(buffer, payload).AsTask().GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    private static byte[] Frames(params byte[][] payloads) => payloads.SelectMany(Frame).ToArray();

    private static async Task<(HostExit Exit, List<string> Replies)> RunAsync(
        byte[] input, Func<InvocationRequest, ForwardResult> forward)
    {
        using var reader = new MemoryStream(input);
        using var writer = new MemoryStream();
        var exit = await new NativeMessagingHost(forward).RunAsync(reader, writer);

        var replies = new List<string>();
        writer.Position = 0;
        while (await IpcFraming.ReadFrameAsync(writer, 1 << 20) is { } payload)
        {
            replies.Add(Encoding.UTF8.GetString(payload));
        }

        return (exit, replies);
    }

    private static ForwardResult Unused(InvocationRequest request) => throw new InvalidOperationException("Nothing should be forwarded.");

    [Fact]
    public async Task APingIsAnsweredAtOnce_AndNothingIsForwarded()
    {
        var (exit, replies) = await RunAsync(Frames(BrowserMessageProtocol.EncodePing()), Unused);

        Assert.Equal(HostExit.InputClosed, exit);
        Assert.Equal(["{\"v\":1,\"type\":\"pong\"}"], replies);
    }

    [Fact]
    public async Task ASelectionIsForwardedToTheAppAsAnInvocationRequest_AndAcceptedWhenTheAppTakesIt()
    {
        InvocationRequest? forwarded = null;

        var (exit, replies) = await RunAsync(
            Frames(BrowserMessageProtocol.EncodeSelection(Sample)),
            request =>
            {
                forwarded = request;
                return ForwardResult.Forwarded;
            });

        Assert.Equal(HostExit.InputClosed, exit);
        Assert.Equal(["{\"v\":1,\"type\":\"accepted\"}"], replies);
        Assert.Equal(InvocationAction.AskAboutBrowserSelection, forwarded!.Action);
        Assert.Equal(Sample, forwarded.Selection);
    }

    [Theory]
    [InlineData(ForwardResult.AppNotFound, "appNotFound")]
    [InlineData(ForwardResult.AppDidNotRespond, "appDidNotRespond")]
    [InlineData(ForwardResult.Refused, "appRefused")]
    public async Task WhenTheAppDoesNotTakeTheSelectionTheReplyNamesWhy_WithoutAnyOfItsText(ForwardResult result, string code)
    {
        var (_, replies) = await RunAsync(Frames(BrowserMessageProtocol.EncodeSelection(Sample)), _ => result);

        var reply = Assert.Single(replies);
        Assert.Equal($"{{\"v\":1,\"type\":\"error\",\"body\":{{\"code\":\"{code}\"}}}}", reply);
        Assert.DoesNotContain("selected", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("example", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForwardingThatThrowsIsAnAppThatDidNotRespond_AndTheLoopGoesOn()
    {
        var calls = 0;
        var (exit, replies) = await RunAsync(
            Frames(BrowserMessageProtocol.EncodeSelection(Sample), BrowserMessageProtocol.EncodePing()),
            _ =>
            {
                calls++;
                throw new IOException("the pipe broke");
            });

        Assert.Equal(HostExit.InputClosed, exit);
        Assert.Equal(1, calls);
        Assert.Equal(
            ["{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"appDidNotRespond\"}}", "{\"v\":1,\"type\":\"pong\"}"], replies);
    }

    [Fact]
    public async Task MessagesAreServedInOrder_AMessageThatCannotBeReadIsAnsweredWithAnError_AndTheLoopGoesOn()
    {
        var forwarded = new List<string>();
        var input = Frames(
            BrowserMessageProtocol.EncodeSelection(Sample with { Text = "first" }),
            Encoding.UTF8.GetBytes("this is not json"),
            Encoding.UTF8.GetBytes("{\"v\":2,\"type\":\"selection\"}"),
            Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"readHistory\"}"),
            Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"  \"}}"),
            BrowserMessageProtocol.EncodeSelection(Sample with { Text = "second" }));

        var (exit, replies) = await RunAsync(input, request =>
        {
            forwarded.Add(request.Selection!.Text);
            return ForwardResult.Forwarded;
        });

        Assert.Equal(HostExit.InputClosed, exit);
        Assert.Equal(["first", "second"], forwarded);
        Assert.Equal(
            [
                "{\"v\":1,\"type\":\"accepted\"}",
                "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"malformed\"}}",
                "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"unsupportedProtocolVersion\"}}",
                "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"unknownType\"}}",
                "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"malformed\"}}",
                "{\"v\":1,\"type\":\"accepted\"}",
            ],
            replies);
    }

    [Fact]
    public async Task NoInputEndsTheLoopCleanly()
    {
        var (exit, replies) = await RunAsync([], Unused);

        Assert.Equal(HostExit.InputClosed, exit);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 })] // a frame is never empty
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })] // longer than the limit: nothing is allocated for it
    [InlineData(new byte[] { 0x10, 0, 0, 0, 0x41 })] // cut short
    [InlineData(new byte[] { 1, 2 })] // the header cut short
    public async Task AFrameThatBreaksTheFramingEndsTheLoopWithoutAReply(byte[] input)
    {
        var (exit, replies) = await RunAsync(input, Unused);

        Assert.Equal(HostExit.InputBroken, exit);
        Assert.Empty(replies);
    }

    [Fact]
    public async Task ABrokenFrameAfterGoodMessagesStillLeavesTheirRepliesWritten()
    {
        var input = Frames(BrowserMessageProtocol.EncodePing()).Concat(new byte[] { 0, 0, 0, 0 }).ToArray();

        var (exit, replies) = await RunAsync(input, Unused);

        Assert.Equal(HostExit.InputBroken, exit);
        Assert.Equal(["{\"v\":1,\"type\":\"pong\"}"], replies);
    }

    [Fact]
    public async Task AnOutputTheBrowserStoppedReadingEndsTheLoop()
    {
        using var reader = new MemoryStream(Frames(BrowserMessageProtocol.EncodePing(), BrowserMessageProtocol.EncodePing()));

        var exit = await new NativeMessagingHost(Unused).RunAsync(reader, new BrokenStream());

        Assert.Equal(HostExit.OutputClosed, exit);
    }

    [Fact]
    public async Task ALargeSelectionPassesThroughWhole()
    {
        var text = new string('x', BrowserSelection.MaxTextLength - 1) + "é";
        var big = new BrowserSelection(text, true, new string('t', BrowserSelection.MaxTitleLength), new string('u', BrowserSelection.MaxUrlLength));
        BrowserSelection? received = null;

        var (_, replies) = await RunAsync(Frames(BrowserMessageProtocol.EncodeSelection(big)), request =>
        {
            received = request.Selection;
            return ForwardResult.Forwarded;
        });

        Assert.Equal(["{\"v\":1,\"type\":\"accepted\"}"], replies);
        Assert.Equal(big, received);
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new IOException("The pipe is being closed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => throw new IOException("The pipe is being closed.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The pipe is being closed.");

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The pipe is being closed.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
