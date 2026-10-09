using System.IO.Pipes;
using System.Text;
using Assistant.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The running app's end of its pipe (PROJECT_SPEC §5.7), over real named pipes with names of their own.</summary>
public sealed class InvocationServerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ARequestIsHandedToTheHandlerAndAnswered()
    {
        var handler = new RecordingHandler();
        await using var server = Serve(handler);

        var reply = await SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt", @"C:\b.png"]));

        Assert.True(reply.IsAccepted);
        var request = Assert.Single(handler.Requests);
        Assert.Equal([@"C:\a.txt", @"C:\b.png"], request.Paths);
    }

    [Fact]
    public async Task TheHandlersRefusalAndFailureAreReplies()
    {
        var handler = new RecordingHandler { Reply = new InvocationReply(InvocationErrorCode.Unavailable) };
        await using var server = Serve(handler);
        Assert.Equal(InvocationErrorCode.Unavailable, (await SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt"]))).Error);

        handler.Throw = true;
        Assert.Equal(InvocationErrorCode.Unavailable, (await SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt"]))).Error);
    }

    [Fact]
    public async Task AMalformedRequestIsAnsweredWithItsCode_AndNeverReachesTheHandler_AndTheServerGoesOn()
    {
        var handler = new RecordingHandler();
        await using var server = Serve(handler);

        foreach (var (json, code) in new[]
                 {
                     ("{\"v\":9,\"type\":\"askAboutFiles\"}", InvocationErrorCode.UnsupportedProtocolVersion),
                     ("{\"v\":1,\"type\":\"executeTool\",\"body\":{}}", InvocationErrorCode.UnknownAction),
                     ("garbage", InvocationErrorCode.Malformed),
                 })
        {
            using var pipe = await ConnectAsync(server.Name);
            await IpcFraming.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(json));
            var reply = await IpcFraming.ReadFrameAsync(pipe, InvocationProtocol.MaxFrameLength);
            Assert.Equal(code, InvocationProtocol.DecodeReply(reply!).Error);
        }

        // A frame longer than allowed breaks the connection, and the next client is served all the same.
        using (var pipe = await ConnectAsync(server.Name))
        {
            await pipe.WriteAsync(BitConverter.GetBytes(InvocationProtocol.MaxFrameLength + 1));
            await pipe.FlushAsync();
            Assert.Null(await IpcFraming.ReadFrameAsync(pipe, InvocationProtocol.MaxFrameLength));
        }

        Assert.True((await SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt"]))).IsAccepted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AClientThatSendsNothingIsDroppedAfterTheTimeout_WithoutHoldingUpOthers()
    {
        var handler = new RecordingHandler();
        await using var server = Serve(handler, TimeSpan.FromMilliseconds(300));

        using var silent = await ConnectAsync(server.Name);
        Assert.True((await SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt"]))).IsAccepted);

        // The silent one's connection is closed by the server: its read ends.
        var read = await silent.ReadAsync(new byte[1]).AsTask().WaitAsync(Wait);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ManyClientsAtOnceAreAllServed()
    {
        var handler = new RecordingHandler();
        await using var server = Serve(handler);

        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            SendAsync(server.Name, new(InvocationAction.AskAboutFiles, [$@"C:\file{i}.txt"]))));

        Assert.All(replies, reply => Assert.True(reply.IsAccepted));
        Assert.Equal(20, handler.Requests.Count);
    }

    [Fact]
    public async Task ASecondServerForTheSameNameServesNothing_AndStoppingEndsTheFirst()
    {
        await using var first = Serve(new RecordingHandler());
        await SendAsync(first.Name, new(InvocationAction.AskAboutFiles, [@"C:\a.txt"]));

        var second = new InvocationServer(first.Name, new RecordingHandler(), NullLogger<InvocationServer>.Instance);
        Assert.Equal(InvocationServerExit.PipeUnavailable, await second.RunAsync(CancellationToken.None).WaitAsync(Wait));

        first.Stop.Cancel();
        Assert.Equal(InvocationServerExit.Stopped, await first.Serving.WaitAsync(Wait));
    }

    private static Running Serve(IInvocationHandler handler, TimeSpan? timeout = null)
    {
        var name = LocalPipe.CreateUniqueName("Assistant.Tests.App");
        var stop = new CancellationTokenSource();
        var server = new InvocationServer(name, handler, NullLogger<InvocationServer>.Instance, timeout);
        return new Running(name, stop, Task.Run(() => server.RunAsync(stop.Token)));
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
        await pipe.ConnectAsync((int)Wait.TotalMilliseconds);
        return pipe;
    }

    private static async Task<InvocationReply> SendAsync(string name, InvocationRequest request)
    {
        using var pipe = await ConnectAsync(name);
        return await InvocationClient.SendAsync(pipe, request).WaitAsync(Wait);
    }

    private sealed record Running(string Name, CancellationTokenSource Stop, Task<InvocationServerExit> Serving) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            await Serving.WaitAsync(Wait);
            Stop.Dispose();
        }
    }

    private sealed class RecordingHandler : IInvocationHandler
    {
        private readonly List<InvocationRequest> _requests = [];

        public InvocationReply Reply { get; init; } = InvocationReply.Accepted;

        public bool Throw { get; set; }

        public IReadOnlyList<InvocationRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public InvocationReply Handle(InvocationRequest request)
        {
            if (Throw)
            {
                throw new InvalidOperationException("The handler failed.");
            }

            lock (_requests)
            {
                _requests.Add(request);
            }

            return Reply;
        }
    }
}
