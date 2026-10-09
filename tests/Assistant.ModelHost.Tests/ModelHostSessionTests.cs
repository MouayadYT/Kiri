using System.Text;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The host's side of one connection, over a real named pipe, with the app's side driven by hand.</summary>
public sealed class ModelHostSessionTests
{
    [Fact]
    public async Task Ping_IsAnsweredWithTheHostsHealth()
    {
        await using var connection = await Connection.StartAsync();
        await using var client = new ModelHostClient(connection.TakeApp(), NullLogger<ModelHostClient>.Instance);

        var report = await client.PingAsync(TestPipes.Timeout());

        Assert.Equal(Environment.ProcessId, report.ProcessId);
        Assert.Equal(typeof(ModelHostRequestHandler).Assembly.GetName().Version, report.HostVersion);
        Assert.True(report.Uptime >= TimeSpan.Zero);
        Assert.True(report.WorkingSetBytes > 0);
        Assert.Null(report.LoadedModelId);
    }

    [Fact]
    public async Task Pings_AreAnsweredOneForOne()
    {
        await using var connection = await Connection.StartAsync();
        await using var client = new ModelHostClient(connection.TakeApp(), NullLogger<ModelHostClient>.Instance);

        var reports = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.PingAsync(TestPipes.Timeout())));

        Assert.All(reports, report => Assert.Equal(Environment.ProcessId, report.ProcessId));
    }

    [Theory]
    [InlineData(nameof(GenerateTextRequest), ModelHostErrorCode.ModelNotFound)]
    [InlineData(nameof(GenerateMultimodalRequest), ModelHostErrorCode.ModelNotFound)]
    public async Task Generating_WithoutAModel_IsRefused(string typeName, ModelHostErrorCode expected)
    {
        await using var connection = await Connection.StartAsync();

        await connection.App.SendAsync(12, SampleMessages.Get(typeName), TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        Assert.Equal(12, reply!.Id);
        Assert.Equal(new ModelHostError(expected), reply.Message);
    }

    [Fact]
    public async Task Cancel_HasNoReplyOfItsOwn()
    {
        await using var connection = await Connection.StartAsync();

        await connection.App.SendAsync(1, new CancelGenerationRequest(99), TestPipes.Timeout());
        await connection.App.SendAsync(2, new HealthRequest(), TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        Assert.Equal(2, reply!.Id);
        Assert.IsType<HealthReport>(reply.Message);
    }

    [Fact]
    public async Task Cancel_StopsTheGenerationItNames()
    {
        var handler = new WaitingHandler();
        await using var connection = await Connection.StartAsync(handler);

        await connection.App.SendAsync(1, SampleMessages.GenerateText, TestPipes.Timeout());
        await handler.Started.Task.WaitAsync(TestPipes.Timeout());
        await connection.App.SendAsync(2, new CancelGenerationRequest(1), TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        Assert.Equal(1, reply!.Id);
        Assert.Equal(new GenerationEnded(GenerationStopReason.Cancelled), reply.Message);
    }

    [Fact]
    public async Task Shutdown_EndsRunningRequests_ThenIsAccepted_ThenCloses()
    {
        var handler = new WaitingHandler();
        await using var connection = await Connection.StartAsync(handler);

        await connection.App.SendAsync(1, SampleMessages.GenerateText, TestPipes.Timeout());
        await handler.Started.Task.WaitAsync(TestPipes.Timeout());
        await connection.App.SendAsync(2, new ShutdownRequest(), TestPipes.Timeout());

        var first = await connection.App.ReceiveAsync(TestPipes.Timeout());
        var second = await connection.App.ReceiveAsync(TestPipes.Timeout());
        var end = await connection.App.ReceiveAsync(TestPipes.Timeout());

        AssertReply(first, 1, new GenerationEnded(GenerationStopReason.Cancelled));
        AssertReply(second, 2, new ShutdownAccepted());
        Assert.Null(end);
        Assert.Equal(ModelHostExitReason.ShutdownRequested, await connection.Session.WaitAsync(TestPipes.Timeout()));
    }

    [Fact]
    public async Task ShutdownFromTheClient_WaitsForAcceptance()
    {
        await using var connection = await Connection.StartAsync();
        await using var client = new ModelHostClient(connection.TakeApp(), NullLogger<ModelHostClient>.Instance);

        await client.ShutdownAsync(TestPipes.Timeout());

        Assert.Equal(ModelHostExitReason.ShutdownRequested, await connection.Session.WaitAsync(TestPipes.Timeout()));
        await client.Completion.WaitAsync(TestPipes.Timeout());
    }

    [Fact]
    public async Task OwnerClosingTheConnection_EndsTheSession()
    {
        await using var connection = await Connection.StartAsync();

        await connection.App.DisposeAsync();

        Assert.Equal(ModelHostExitReason.OwnerDisconnected, await connection.Session.WaitAsync(TestPipes.Timeout()));
    }

    [Fact]
    public async Task HandlerFailure_IsAnsweredWithAnInternalError()
    {
        await using var connection = await Connection.StartAsync(new ThrowingHandler());

        await connection.App.SendAsync(3, new HealthRequest(), TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        AssertReply(reply, 3, new ModelHostError(ModelHostErrorCode.Internal));
    }

    [Theory]
    [InlineData("""{"v":2,"id":8,"type":"health","body":{}}""", 8, ModelHostErrorCode.UnsupportedProtocolVersion)]
    [InlineData("""{"v":1,"id":8,"type":"teleport","body":{}}""", 8, ModelHostErrorCode.UnknownMessageType)]
    [InlineData("""{"v":1,"id":8,"type":"healthReport","body":{"hostVersion":"1.0","processId":1,"uptime":"00:00:01"}}""", 8, ModelHostErrorCode.UnknownMessageType)]
    [InlineData("""{"v":1,"id":0,"type":"health","body":{}}""", 0, ModelHostErrorCode.MalformedMessage)]
    [InlineData("""{"v":1,"id":8,"type":"loadModel","body":{}}""", 8, ModelHostErrorCode.MalformedMessage)]
    [InlineData("""{not json""", 0, ModelHostErrorCode.MalformedMessage)]
    public async Task UnreadableFrames_AreAnswered_AndTheSessionGoesOn(string json, long id, ModelHostErrorCode error)
    {
        await using var connection = await Connection.StartAsync();

        await IpcFraming.WriteFrameAsync(connection.AppStream, Encoding.UTF8.GetBytes(json), TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());
        await connection.App.SendAsync(9, new HealthRequest(), TestPipes.Timeout());
        var next = await connection.App.ReceiveAsync(TestPipes.Timeout());

        AssertReply(reply, id, new ModelHostError(error));
        Assert.Equal(9, next!.Id);
        Assert.IsType<HealthReport>(next.Message);
    }

    [Fact]
    public async Task RequestId_ThatIsStillRunning_IsRejected()
    {
        var handler = new WaitingHandler();
        await using var connection = await Connection.StartAsync(handler);

        await connection.App.SendAsync(4, SampleMessages.GenerateText, TestPipes.Timeout());
        await handler.Started.Task.WaitAsync(TestPipes.Timeout());
        await connection.App.SendAsync(4, SampleMessages.GenerateText, TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        AssertReply(reply, 4, new ModelHostError(ModelHostErrorCode.MalformedMessage));
    }

    [Fact]
    public async Task BrokenFraming_ClosesTheConnection()
    {
        await using var connection = await Connection.StartAsync();

        await connection.AppStream.WriteAsync(BitConverter.GetBytes(ModelHostProtocol.MaxFrameLength + 1), TestPipes.Timeout());
        await connection.AppStream.FlushAsync(TestPipes.Timeout());

        Assert.Equal(ModelHostExitReason.ProtocolViolation, await connection.Session.WaitAsync(TestPipes.Timeout()));
        Assert.Null(await connection.App.ReceiveAsync(TestPipes.Timeout()));
    }

    [Fact]
    public async Task LargeImages_FitInOneFrame()
    {
        await using var connection = await Connection.StartAsync();
        var screenshot = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(screenshot);
        var request = SampleMessages.GenerateMultimodal with { Images = [screenshot, screenshot] };

        await connection.App.SendAsync(5, request, TestPipes.Timeout());
        var reply = await connection.App.ReceiveAsync(TestPipes.Timeout());

        // It arrived whole and was read: without a model loaded, it is refused as such.
        AssertReply(reply, 5, new ModelHostError(ModelHostErrorCode.ModelNotFound));
    }

    private static void AssertReply(ModelHostFrame? frame, long id, ModelHostMessage message)
    {
        Assert.NotNull(frame);
        Assert.Equal(id, frame.Id);
        Assert.Equal(message, frame.Message);
    }

    /// <summary>A session serving the host's end of a pipe, and the app's end as a raw channel.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Stream? _appStream;

        private Connection(Stream appStream, Stream hostStream, ModelHostSession session)
        {
            _appStream = appStream;
            App = new ModelHostChannel(appStream);
            Session = session.RunAsync(hostStream, _stop.Token);
        }

        public ModelHostChannel App { get; }

        public Stream AppStream => _appStream ?? throw new InvalidOperationException("The app's end was taken.");

        public Task<ModelHostExitReason> Session { get; }

        public static async Task<Connection> StartAsync(IModelHostRequestHandler? handler = null)
        {
            var (host, app) = await TestPipes.ConnectAsync();
            var session = new ModelHostSession(
                handler ?? TestHandlers.Create(new NullModelController()),
                NullLogger<ModelHostSession>.Instance);
            return new Connection(app, host, session);
        }

        /// <summary>Hands the app's end to a <see cref="ModelHostClient"/>, which then owns it.</summary>
        public Stream TakeApp()
        {
            var stream = AppStream;
            _appStream = null;
            return stream;
        }

        public async ValueTask DisposeAsync()
        {
            if (_appStream is not null)
            {
                await App.DisposeAsync();
            }

            _stop.Cancel();
            await Session.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    /// <summary>Starts every request and then waits, as a long generation does, until it is cancelled.</summary>
    private sealed class WaitingHandler : IModelHostRequestHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(ModelHostRequest request, IModelHostReplies replies, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class ThrowingHandler : IModelHostRequestHandler
    {
        public Task HandleAsync(ModelHostRequest request, IModelHostReplies replies, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The engine fell over.");
    }
}
