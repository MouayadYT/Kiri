using Assistant.Core.Ipc;
using Assistant.ExplorerExtension.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ExplorerExtension.Tests;

/// <summary>
/// Handing File Explorer's files to the app (PROJECT_SPEC §4.4, §5.7), over real named pipes with names of their own, with an
/// app that is a server in this process: running, started on demand, missing or silent. The real app is never started.
/// </summary>
public sealed class InvocationForwarderTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _pipeName = LocalPipe.CreateUniqueName("Assistant.Tests.Explorer");
    private readonly ForwarderOptions _options = new()
    {
        StartLockName = $@"Local\Assistant.Tests.Start.{Guid.NewGuid():N}",
        ConnectTimeout = TimeSpan.FromMilliseconds(200),
        StartTimeout = TimeSpan.FromSeconds(10),
        StartingTimeout = TimeSpan.FromMilliseconds(600),
        ReplyTimeout = TimeSpan.FromSeconds(5),
    };

    private readonly RecordingHandler _handler = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _serving;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_serving is not null)
        {
            await _serving.WaitAsync(Wait);
        }

        _stop.Dispose();
    }

    [Fact]
    public void TheFilesReachTheRunningApp_WhichIsNotStartedAgain()
    {
        StartApp();
        var starter = new FakeStarter(this, running: true);

        var result = Forwarder(starter).Forward(Ask(@"C:\Docs\plan.docx"));

        Assert.Equal(ForwardResult.Forwarded, result);
        Assert.Equal([@"C:\Docs\plan.docx"], Assert.Single(_handler.Requests).Paths);
        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public void AnAppThatIsNotRunningIsStarted_AndGetsTheFilesOnceItListens()
    {
        var starter = new FakeStarter(this, running: false, startDelay: TimeSpan.FromMilliseconds(400));

        var result = Forwarder(starter).Forward(Ask(@"C:\Docs\plan.docx"));

        Assert.Equal(ForwardResult.Forwarded, result);
        Assert.Equal(1, starter.Starts);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task FiveFilesAtOnceStartTheAppOnce_AndAllReachIt()
    {
        var starter = new FakeStarter(this, running: false, startDelay: TimeSpan.FromMilliseconds(500));

        var results = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            Task.Run(() => Forwarder(starter).Forward(Ask($@"C:\Docs\note{i}.md"))))).WaitAsync(Wait);

        Assert.All(results, result => Assert.Equal(ForwardResult.Forwarded, result));
        Assert.Equal(1, starter.Starts);
        Assert.Equal(5, _handler.Requests.Count);
    }

    [Fact]
    public void AnAppThatCannotBeFoundIsReported()
    {
        var starter = new FakeStarter(this, running: false) { CanStart = false };
        Assert.Equal(ForwardResult.AppNotFound, Forwarder(starter).Forward(Ask(@"C:\a.txt")));
        Assert.NotNull(FailureMessage.For(ForwardResult.AppNotFound));
    }

    [Fact]
    public void ARunningAppThatNeverListensIsReported_AndNotStartedAgain()
    {
        var starter = new FakeStarter(this, running: true);
        Assert.Equal(ForwardResult.AppDidNotRespond, Forwarder(starter).Forward(Ask(@"C:\a.txt")));
        Assert.Equal(0, starter.Starts);
        Assert.NotNull(FailureMessage.For(ForwardResult.AppDidNotRespond));
    }

    [Fact]
    public void AnAppThatRefusesIsReported()
    {
        _handler.Reply = new InvocationReply(InvocationErrorCode.Unavailable);
        StartApp();
        Assert.Equal(ForwardResult.Refused, Forwarder(new FakeStarter(this, running: true)).Forward(Ask(@"C:\a.txt")));
        Assert.NotNull(FailureMessage.For(ForwardResult.Refused));
        Assert.Null(FailureMessage.For(ForwardResult.Forwarded));
    }

    private static InvocationRequest Ask(string path) => new(InvocationAction.AskAboutFiles, [path]);

    private InvocationForwarder Forwarder(IAppStarter starter) => new(_pipeName, starter, _options);

    private void StartApp()
    {
        var server = new InvocationServer(_pipeName, _handler, NullLogger<InvocationServer>.Instance);
        _serving = Task.Run(() => server.RunAsync(_stop.Token));
    }

    private sealed class FakeStarter(InvocationForwarderTests test, bool running, TimeSpan? startDelay = null) : IAppStarter
    {
        private int _starts;
        private bool _running = running;

        public bool CanStart { get; init; } = true;

        public int Starts => Volatile.Read(ref _starts);

        public bool IsRunning() => Volatile.Read(ref _running);

        public bool Start()
        {
            if (!CanStart)
            {
                return false;
            }

            Interlocked.Increment(ref _starts);
            Volatile.Write(ref _running, true);

            // The app takes a moment to start before it listens.
            _ = Task.Delay(startDelay ?? TimeSpan.Zero).ContinueWith(_ => test.StartApp(), TaskScheduler.Default);
            return true;
        }
    }

    private sealed class RecordingHandler : IInvocationHandler
    {
        private readonly List<InvocationRequest> _requests = [];

        public InvocationReply Reply { get; set; } = InvocationReply.Accepted;

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
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Reply;
        }
    }
}
