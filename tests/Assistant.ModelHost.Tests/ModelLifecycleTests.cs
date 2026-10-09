using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// The app's side of loading: the client, the lifecycle and the events, over a real pipe to a real session, controller
/// and manager, with the fake engine as the engine.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    [Fact]
    public async Task Load_StartsTheHost_PublishesLoadingThenReady_AndReturnsTheModel()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);

        var model = await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        await statuses.WaitForAsync(ModelStatus.Ready);

        Assert.Equal("model", model.Id);
        Assert.Equal(1, host.Launcher.Started);
        Assert.Equal([ModelStatus.Loading, ModelStatus.Ready], statuses.Values.Select(status => status.Status));
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
        Assert.Equal("model", lifecycle.Current.ModelId);
    }

    [Fact]
    public async Task WhenLoadReturns_TheStatusAlreadyShowsIt()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        for (var round = 0; round < 5; round++)
        {
            await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
            Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);

            await lifecycle.UnloadAsync(TestPipes.Timeout());
            Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        }
    }

    [Fact]
    public async Task ASecondLoad_ReusesTheHost()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var other = setup.CreateScenario("other.gguf", new FakeEngineScenario());

        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        var second = await lifecycle.LoadAsync(new ModelFiles(other), TestPipes.Timeout());

        Assert.Equal("other", second.Id);
        Assert.Equal(1, host.Launcher.Started);
        Assert.Equal("other", lifecycle.Current.ModelId);
    }

    [Fact]
    public async Task Load_OfAModelWithAProjector_AdvertisesVision()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var files = new ModelFiles(setup.ScenarioPath) { ProjectorPath = setup.CreateGguf("mmproj.gguf") };

        var model = await lifecycle.LoadAsync(files, TestPipes.Timeout());

        Assert.True(model.SupportsVision);
    }

    [Fact]
    public async Task Load_OfAMissingFile_FailsWithTheReason_AndCanBeRetried()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);

        var failed = await Assert.ThrowsAsync<ModelHostException>(() =>
            lifecycle.LoadAsync(new ModelFiles(Path.Combine(setup.Directory, "gone.gguf")), TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.ModelNotFound, failed.Code);
        Assert.Equal(ModelStatus.Failed, lifecycle.Current.Status);
        Assert.Equal(ModelFailure.ModelNotFound, lifecycle.Current.Failure);
        Assert.Contains("model file", ModelStatusText.Describe(lifecycle.Current.Status, lifecycle.Current.Failure), StringComparison.Ordinal);

        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
        Assert.Null(lifecycle.Current.Failure);
        Assert.Equal(1, host.Launcher.Started);
        Assert.Equal(
            [ModelStatus.Loading, ModelStatus.Failed, ModelStatus.Loading, ModelStatus.Ready],
            statuses.Values.Select(status => status.Status));
    }

    [Fact]
    public async Task Load_WhenTheHostCannotBeStarted_FailsWithHostUnavailable_AndTriesAgainNextTime()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup) { Launcher = { FailNext = true } };
        await using var lifecycle = host.CreateLifecycle(out _);

        await Assert.ThrowsAsync<ModelHostException>(
            () => lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout()));

        Assert.Equal(ModelStatus.Failed, lifecycle.Current.Status);
        Assert.Equal(ModelFailure.HostUnavailable, lifecycle.Current.Failure);

        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
    }

    [Fact]
    public async Task Unload_ReportsUnloadingThenNotLoaded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());

        await lifecycle.UnloadAsync(TestPipes.Timeout());

        Assert.Equal(
            [ModelStatus.Loading, ModelStatus.Ready, ModelStatus.Unloading, ModelStatus.NotLoaded],
            statuses.Values.Select(status => status.Status));
        Assert.Equal(ModelProcessState.Stopped, host.Manager.State);
    }

    [Fact]
    public async Task Unload_WithoutAHost_DoesNothing()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);

        await lifecycle.UnloadAsync(TestPipes.Timeout());

        Assert.Equal(0, host.Launcher.Started);
        Assert.Empty(statuses.Values);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
    }

    [Fact]
    public async Task ALoadReplacedByAnotherLoad_FailsAsCancelled_WhileTheOtherSucceeds()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        var good = setup.CreateScenario("good.gguf", new FakeEngineScenario());
        var first = lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        await statuses.WaitForAsync(ModelStatus.Loading);
        await WaitUntilAsync(() => setup.Launches >= 1);

        var second = await lifecycle.LoadAsync(new ModelFiles(good), TestPipes.Timeout());

        var replaced = await Assert.ThrowsAsync<ModelHostException>(() => first);
        Assert.Equal(ModelHostErrorCode.Cancelled, replaced.Code);
        Assert.Equal("good", second.Id);
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
    }

    [Fact]
    public async Task CancellingALoad_UnloadsTheModel()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        using var cancel = new CancellationTokenSource();
        var loading = lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), cancel.Token);
        await statuses.WaitForAsync(ModelStatus.Loading);
        await WaitUntilAsync(() => setup.Launches >= 1 && host.Controller.Current.Status == ModelStatus.Loading);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        await statuses.WaitForAsync(ModelStatus.NotLoaded);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        Assert.Equal(ModelProcessState.Stopped, host.Manager.State);
    }

    [Fact]
    public async Task AnEngineThatCrashesAndRestarts_IsShownToTheApp()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = 1 });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);

        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        await WaitUntilAsync(() => statuses.Values.Count >= 4);

        Assert.Equal(
            [ModelStatus.Loading, ModelStatus.Ready, ModelStatus.Loading, ModelStatus.Ready],
            statuses.Values.Select(status => status.Status).Take(4));
    }

    [Fact]
    public async Task AHostThatGoesAway_FailsTheModel_AndTheNextLoadStartsANewHost()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());

        await host.Launcher.Last!.CrashAsync();
        await statuses.WaitForAsync(ModelStatus.Failed);

        Assert.Equal(ModelFailure.HostUnavailable, lifecycle.Current.Failure);
        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        Assert.Equal(2, host.Launcher.Started);
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
    }

    [Fact]
    public async Task TheHostsHealthReport_SaysWhatIsLoaded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var before = await host.PingAsync();

        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        var after = await host.PingAsync();

        Assert.Equal(ModelStatus.NotLoaded, before.ModelStatus);
        Assert.Null(before.LoadedModelId);
        Assert.Equal(ModelStatus.Ready, after.ModelStatus);
        Assert.Equal("model", after.LoadedModelId);
    }

    [Fact]
    public async Task Disposing_EndsTheHost_AndTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        var lifecycle = host.CreateLifecycle(out _);
        await lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        var processId = setup.Report(1).ProcessId;

        await lifecycle.DisposeAsync();
        await host.Launcher.Last!.Ended.WaitAsync(TestPipes.Timeout());
        await host.Manager.StopAsync();

        Assert.False(TestRuntimes.IsRunning(processId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>What a host does in the app's process for these tests: a session over a pipe, with a real controller.</summary>
    private sealed class TestHost : IAsyncDisposable
    {
        public TestHost(FakeEngineSetup setup)
        {
            Manager = setup.CreateManager();
            Controller = new ModelController(Manager, TimeProvider.System, NullLogger<ModelController>.Instance);
            Engine = new LlamaServerChatEngine(Manager);
            Handler = TestHandlers.Create(Controller, Engine);
            Launcher = new InProcessLauncher(Handler, Controller);
        }

        public ModelProcessManager Manager { get; }

        public ModelController Controller { get; }

        public LlamaServerChatEngine Engine { get; }

        public ModelHostRequestHandler Handler { get; }

        public InProcessLauncher Launcher { get; }

        public ModelLifecycle CreateLifecycle(out StatusRecorder statuses)
        {
            var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
            statuses = new StatusRecorder(bus);
            return new ModelLifecycle(Launcher, bus, NullLogger<ModelLifecycle>.Instance);
        }

        public async Task<HealthReport> PingAsync()
        {
            // A second connection to the same handler, as a health check would be.
            await using var connection = await Launcher.StartAsync(TestPipes.Timeout());
            return await connection.Client.PingAsync(TestPipes.Timeout());
        }

        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            await Manager.DisposeAsync();
            Engine.Dispose();
        }
    }

    /// <summary>Starts hosts in this process: each is a <see cref="ModelHostSession"/> on one end of a real pipe.</summary>
    private sealed class InProcessLauncher(ModelHostRequestHandler handler, ModelController controller) : IModelHostLauncher
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public bool FailNext { get; set; }

        public InProcessHost? Last { get; private set; }

        public async Task<IModelHostConnection> StartAsync(CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new ModelHostException("The model host could not be started.");
            }

            Interlocked.Increment(ref _started);
            var (hostStream, appStream) = await TestPipes.ConnectAsync();
            var session = new ModelHostSession(handler, NullLogger<ModelHostSession>.Instance, controller);
            var client = new ModelHostClient(appStream, NullLogger<ModelHostClient>.Instance);
            Last = new InProcessHost(client, hostStream, session);
            return Last;
        }
    }

    private sealed class InProcessHost : IModelHostConnection
    {
        private readonly Stream _hostStream;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<ModelHostExitReason> _serving;

        public InProcessHost(ModelHostClient client, Stream hostStream, ModelHostSession session)
        {
            Client = client;
            _hostStream = hostStream;
            _serving = session.RunAsync(hostStream, _stop.Token);
        }

        public ModelHostClient Client { get; }

        /// <summary>Completes when the host has stopped serving.</summary>
        public Task Ended => _serving;

        /// <summary>Ends the host the way a crash does: without a word, the pipe just closes.</summary>
        public async Task CrashAsync()
        {
            await _stop.CancelAsync();
            await _hostStream.DisposeAsync();
            await Client.Completion.WaitAsync(TestPipes.Timeout());
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serving.WaitAsync(TestPipes.Timeout());
        }
    }

    /// <summary>Records the <see cref="ModelStatusChanged"/> events an app would see.</summary>
    private sealed class StatusRecorder
    {
        private readonly object _lock = new();
        private readonly List<ModelStatusChanged> _values = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        public StatusRecorder(IAppEventBus bus) =>
            _subscription = bus.Subscribe<StatusRecorder, ModelStatusChanged>(this, static (self, status, _) => self.Record(status));

        public IReadOnlyList<ModelStatusChanged> Values
        {
            get
            {
                lock (_lock)
                {
                    return _values.ToArray();
                }
            }
        }

        public async Task WaitForAsync(ModelStatus status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_values.Any(value => value.Status == status))
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(timeout.Token);
            }
        }

        private Task Record(ModelStatusChanged status)
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _values.Add(status);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
