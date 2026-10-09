using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>Loading and unloading a model through the engine, played by the fake engine, and the status it reports.</summary>
public sealed class ModelControllerTests
{
    [Fact]
    public async Task Load_ReportsLoadingThenReady_AndTheModelsCapabilities()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        var model = await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());

        Assert.Equal("chat", model.Id);
        Assert.Equal(2048, model.ContextLength);
        Assert.False(model.SupportsVision);
        Assert.True(model.SupportsConstrainedOutput);
        Assert.Equal(
            [ModelStatus.Loading, ModelStatus.Ready], rig.Statuses.Select(report => report.Status));
        Assert.Equal([1L, 2L], rig.Statuses.Select(report => report.Sequence));
        Assert.All(rig.Statuses, report => Assert.Equal("chat", report.ModelId));
        Assert.Equal(model, rig.Controller.Model);
        Assert.Equal(ModelStatus.Ready, rig.Controller.Current.Status);
        Assert.Equal(ModelProcessState.Running, rig.Manager.State);
    }

    [Fact]
    public async Task Load_WithTheGraphicsCardTurnedOff_RunsTheEngineOnTheProcessorAlone()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        await rig.Controller.LoadAsync(new LoadModelRequest("chat") { Files = new ModelFiles(setup.ScenarioPath) { CpuOnly = true } }, TestPipes.Timeout());

        Assert.Equal("none", ValueAfter(setup.Report(1).Arguments, "--device"));
    }

    [Fact]
    public async Task Load_WithTheGraphicsCardOn_LeavesTheChoiceOfDevicesToTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());

        Assert.DoesNotContain("--device", setup.Report(1).Arguments);
    }

    [Fact]
    public void TheGraphicsCardChoiceTravelsInTheLoadRequestAndAnOlderRequestWithoutItKeepsTheCardOn()
    {
        var path = Path.GetFullPath("model.gguf");
        var sent = ModelHostSerializer.Serialize(7, new LoadModelRequest("chat") { Files = new ModelFiles(path) { CpuOnly = true } });
        Assert.Contains("\"cpuOnly\":true", System.Text.Encoding.UTF8.GetString(sent), StringComparison.Ordinal);
        Assert.DoesNotContain("useGpu", System.Text.Encoding.UTF8.GetString(sent), StringComparison.Ordinal);
        var back = Assert.IsType<LoadModelRequest>(ModelHostSerializer.Deserialize(sent).Message);
        Assert.False(back.Files!.UseGpu);

        var older = System.Text.Encoding.UTF8.GetBytes(
            "{\"v\":1,\"id\":7,\"type\":\"loadModel\",\"body\":{\"modelId\":\"chat\",\"files\":{\"modelPath\":" + System.Text.Json.JsonSerializer.Serialize(path) + "}}}");
        var parsed = Assert.IsType<LoadModelRequest>(ModelHostSerializer.Deserialize(older).Message);
        Assert.True(parsed.Files!.UseGpu);
    }

    [Fact]
    public async Task Load_WithAProjectorAndATemplate_GivesTheEngineThemAndAdvertisesVision()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);
        var projector = setup.CreateGguf("mmproj.gguf");
        var template = setup.CreateFile("chat.jinja", "{{ messages }}");
        var files = new ModelFiles(setup.ScenarioPath)
        {
            ProjectorPath = projector,
            ChatTemplatePath = template,
            ContextLength = 4096,
        };

        var model = await rig.Controller.LoadAsync(new LoadModelRequest("vision") { Files = files }, TestPipes.Timeout());

        Assert.True(model.SupportsVision);
        var arguments = setup.Report(1).Arguments;
        Assert.Equal(projector, ValueAfter(arguments, "--mmproj"));
        Assert.Equal(template, ValueAfter(arguments, "--chat-template-file"));
        Assert.Equal("4096", ValueAfter(arguments, "--ctx-size"));
        Assert.Equal("1", ValueAfter(arguments, "--parallel"));
        Assert.DoesNotContain("--no-mmproj-auto", arguments);
    }

    [Fact]
    public async Task Load_WithoutAProjector_KeepsTheEngineFromFindingOne()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());

        var arguments = setup.Report(1).Arguments;
        Assert.Contains("--no-mmproj-auto", arguments);
        Assert.DoesNotContain("--mmproj", arguments);
        Assert.DoesNotContain("--chat-template-file", arguments);
        Assert.Equal(ModelProcessLaunch.DefaultContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueAfter(arguments, "--ctx-size"));
    }

    [Fact]
    public async Task Load_WithoutFiles_IsNotFound_AndChangesNothing()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        var failed = await Assert.ThrowsAsync<ModelRequestException>(
            () => rig.Controller.LoadAsync(new LoadModelRequest("catalog-model"), TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.ModelNotFound, failed.Code);
        Assert.Empty(rig.Statuses);
        Assert.Equal(0, setup.Launches);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("projector")]
    [InlineData("template")]
    public async Task Load_WithAMissingFile_IsNotFound_AndFails(string missing)
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);
        var files = new ModelFiles(setup.ScenarioPath)
        {
            ProjectorPath = setup.CreateGguf("mmproj.gguf"),
            ChatTemplatePath = setup.CreateFile("chat.jinja", "{{ messages }}"),
        };
        var gone = Path.Combine(setup.Directory, "gone.gguf");
        files = missing switch
        {
            "model" => files with { ModelPath = gone },
            "projector" => files with { ProjectorPath = gone },
            _ => files with { ChatTemplatePath = gone },
        };

        var failed = await Assert.ThrowsAsync<ModelRequestException>(
            () => rig.Controller.LoadAsync(new LoadModelRequest("chat") { Files = files }, TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.ModelNotFound, failed.Code);
        Assert.Equal([ModelStatus.Loading, ModelStatus.Failed], rig.Statuses.Select(report => report.Status));
        Assert.Equal(ModelFailure.ModelNotFound, rig.Statuses[^1].Failure);
        Assert.Null(rig.Controller.Model);
        Assert.Equal(0, setup.Launches);
    }

    [Fact]
    public async Task Load_OfAFileThatIsNotGguf_FailsBeforeTheEngineStarts()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);
        var notGguf = setup.CreateFile("notes.gguf", "just some text");
        var empty = setup.CreateFile("empty.gguf", string.Empty);

        foreach (var path in new[] { notGguf, empty })
        {
            var failed = await Assert.ThrowsAsync<ModelRequestException>(
                () => rig.Controller.LoadAsync(rig.Request(path), TestPipes.Timeout()));
            Assert.Equal(ModelHostErrorCode.ModelLoadFailed, failed.Code);
        }

        Assert.Equal(ModelFailure.LoadFailed, rig.Controller.Current.Failure);
        Assert.Equal(0, setup.Launches);
    }

    [Fact]
    public async Task Load_WhenTheEngineCannotLoadTheModel_Fails()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { StartupFailure = "model" });
        await using var rig = Rig.Create(setup);

        var failed = await Assert.ThrowsAsync<ModelRequestException>(
            () => rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.ModelLoadFailed, failed.Code);
        Assert.Equal([ModelStatus.Loading, ModelStatus.Failed], rig.Statuses.Select(report => report.Status));
        Assert.Equal(ModelFailure.LoadFailed, rig.Statuses[^1].Failure);
        Assert.Equal("chat", rig.Statuses[^1].ModelId);
    }

    [Fact]
    public async Task Load_WhenTheRuntimeIsMissing_FailsWithRuntimeUnavailable()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var locator = new StubRuntimeLocator(ModelRuntimeStatus.Unavailable(ModelRuntimeState.NotInstalled));
        await using var manager = new ModelProcessManager(
            locator, setup.Options(), TimeProvider.System, NullLogger<ModelProcessManager>.Instance);
        using var controller = new ModelController(manager, TimeProvider.System, NullLogger<ModelController>.Instance);

        var failed = await Assert.ThrowsAsync<ModelRequestException>(
            () => controller.LoadAsync(new LoadModelRequest("chat") { Files = new ModelFiles(setup.ScenarioPath) }, TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.RuntimeUnavailable, failed.Code);
        Assert.Equal(ModelFailure.RuntimeUnavailable, controller.Current.Failure);
    }

    [Fact]
    public async Task Unload_ReportsUnloadingThenNotLoaded_AndEndsTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);
        await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());
        var processId = rig.Manager.ProcessId!.Value;
        rig.ClearStatuses();

        var unloaded = await rig.Controller.UnloadAsync(TestPipes.Timeout());

        Assert.Equal("chat", unloaded);
        Assert.Equal([ModelStatus.Unloading, ModelStatus.NotLoaded], rig.Statuses.Select(report => report.Status));
        Assert.Null(rig.Statuses[^1].ModelId);
        Assert.Null(rig.Controller.Model);
        Assert.Equal(ModelProcessState.Stopped, rig.Manager.State);
        Assert.False(TestRuntimes.IsRunning(processId));
    }

    [Fact]
    public async Task Unload_WhenNothingIsLoaded_SucceedsQuietly()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = Rig.Create(setup);

        var unloaded = await rig.Controller.UnloadAsync(TestPipes.Timeout());

        Assert.Null(unloaded);
        Assert.Empty(rig.Statuses);
    }

    [Fact]
    public async Task Unload_AfterAFailure_ClearsIt()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { StartupFailure = "model" });
        await using var rig = Rig.Create(setup);
        await Assert.ThrowsAsync<ModelRequestException>(
            () => rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout()));

        var unloaded = await rig.Controller.UnloadAsync(TestPipes.Timeout());

        Assert.Equal("chat", unloaded);
        Assert.Equal(ModelStatus.NotLoaded, rig.Controller.Current.Status);
        Assert.Null(rig.Controller.Current.Failure);
        Assert.Equal(ModelProcessState.Stopped, rig.Manager.State);
    }

    [Fact]
    public async Task ALaterLoad_ReplacesTheOneStillGoing()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var rig = Rig.Create(setup);
        var second = setup.CreateScenario("second.gguf", new FakeEngineScenario());
        var first = rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath, "first"), TestPipes.Timeout());
        await rig.WaitForAsync(report => report.Status == ModelStatus.Loading && report.ModelId == "first");

        var loaded = await rig.Controller.LoadAsync(rig.Request(second, "second"), TestPipes.Timeout());

        var replaced = await Assert.ThrowsAsync<ModelRequestException>(() => first);
        Assert.Equal(ModelHostErrorCode.Cancelled, replaced.Code);
        Assert.Equal("second", loaded.Id);
        Assert.Equal(ModelStatus.Ready, rig.Controller.Current.Status);
        Assert.Equal("second", rig.Controller.Current.ModelId);
        Assert.DoesNotContain(rig.Statuses, report => report.Status == ModelStatus.Failed);
        Assert.Equal(ModelProcessState.Running, rig.Manager.State);
    }

    [Fact]
    public async Task ALoadingModel_CanBeUnloaded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var rig = Rig.Create(setup);
        var loading = rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());
        await rig.WaitForAsync(report => report.Status == ModelStatus.Loading);

        // The manager knows the engine only once it is ready; the fake engine reports itself as it starts.
        await WaitUntilAsync(() => setup.Launches >= 1 && File.Exists(FakeEngineReport.PathOf(setup.ScenarioPath, 1)));
        var processId = setup.Report(1).ProcessId;

        var unloaded = await rig.Controller.UnloadAsync(TestPipes.Timeout());

        var replaced = await Assert.ThrowsAsync<ModelRequestException>(() => loading);
        Assert.Equal(ModelHostErrorCode.Cancelled, replaced.Code);
        Assert.Equal("chat", unloaded);
        Assert.Equal(ModelStatus.NotLoaded, rig.Controller.Current.Status);
        Assert.False(TestRuntimes.IsRunning(processId));
    }

    [Fact]
    public async Task ARequestCancelledByItsCaller_IsNotReportedAsReplaced()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var rig = Rig.Create(setup);
        using var cancel = new CancellationTokenSource();
        var loading = rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), cancel.Token);
        await rig.WaitForAsync(report => report.Status == ModelStatus.Loading);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
    }

    [Fact]
    public async Task AnEngineThatExitsAndIsRestarted_ShowsLoadingAgainThenReady()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = 1 });
        await using var rig = Rig.Create(setup);
        await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());

        await rig.WaitForAsync(report => report.Sequence >= 4);

        Assert.Equal(
            [ModelStatus.Loading, ModelStatus.Ready, ModelStatus.Loading, ModelStatus.Ready],
            rig.Statuses.Select(report => report.Status).Take(4));
        Assert.NotNull(rig.Controller.Model);
    }

    [Fact]
    public async Task AnEngineThatKeepsExiting_EndsFailed()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = -1 });
        await using var rig = Rig.Create(setup, maxRestarts: 1);
        await rig.Controller.LoadAsync(rig.Request(setup.ScenarioPath), TestPipes.Timeout());

        await rig.WaitForAsync(report => report.Status == ModelStatus.Failed);

        Assert.Equal(ModelFailure.EngineStopped, rig.Controller.Current.Failure);
        Assert.Equal("chat", rig.Controller.Current.ModelId);
        Assert.Null(rig.Controller.Model);
    }

    [Fact]
    public async Task Logs_CarryNoPathsOrModelNames()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario(), folderName: "PRIVATE-PATH-0c5a");
        await using var manager = setup.CreateManager(loggers: loggers);
        using var controller = new ModelController(manager, TimeProvider.System, loggers.CreateLogger<ModelController>());
        var files = new ModelFiles(setup.ScenarioPath) { ProjectorPath = setup.CreateGguf("PRIVATE-PATH-0c5a.gguf") };

        await controller.LoadAsync(new LoadModelRequest("PRIVATE-NAME-91ab") { Files = files }, TestPipes.Timeout());
        await controller.UnloadAsync(TestPipes.Timeout());

        Assert.Contains("Model loaded in", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-NAME-91ab", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Directory, capture.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>A controller over a manager over the fake engine, recording the statuses it reports.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly object _lock = new();
        private readonly List<ModelStatusReport> _statuses = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Rig(ModelProcessManager manager, ModelController controller)
        {
            Manager = manager;
            Controller = controller;
            controller.Changed += OnChanged;
        }

        public ModelProcessManager Manager { get; }

        public ModelController Controller { get; }

        /// <summary>The statuses reported so far, oldest first.</summary>
        public IReadOnlyList<ModelStatusReport> Statuses
        {
            get
            {
                lock (_lock)
                {
                    return _statuses.ToArray();
                }
            }
        }

        public void ClearStatuses()
        {
            lock (_lock)
            {
                _statuses.Clear();
            }
        }

        public static Rig Create(FakeEngineSetup setup, int maxRestarts = 3)
        {
            var manager = setup.CreateManager(setup.Options(maxRestarts));
            return new Rig(manager, new ModelController(manager, TimeProvider.System, NullLogger<ModelController>.Instance));
        }

        public LoadModelRequest Request(string modelPath, string id = "chat") =>
            new(id) { Files = new ModelFiles(modelPath) };

        public async Task WaitForAsync(Func<ModelStatusReport, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_statuses.Any(predicate))
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            await Manager.DisposeAsync();
        }

        private void OnChanged(object? sender, ModelStatusReport report)
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _statuses.Add(report);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
        }
    }
}
