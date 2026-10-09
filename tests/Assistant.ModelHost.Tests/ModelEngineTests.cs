using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The engine's output never reaches a log as text (PROJECT_SPEC §3.3).</summary>
public sealed class ModelEnginePrivacyTests
{
    [Fact]
    public async Task EngineOutput_IsLoggedAsSignalsOnly()
    {
        // No privacy filter: the test sees everything the manager hands to its loggers.
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = 1 }, "PRIVATE-PATH-0c5a");
        await using (var manager = setup.CreateManager(loggers: loggers))
        {
            using var states = new StateRecorder(manager);
            await manager.StartAsync(setup.Launch, TestPipes.Timeout());
            await states.WaitForAsync(ModelProcessState.Running, times: 2);
        }

        using var failing = FakeEngineSetup.Create(new FakeEngineScenario { StartupFailure = "model" }, "PRIVATE-PATH-0c5a");
        await using (var manager = failing.CreateManager(loggers: loggers))
        {
            await Assert.ThrowsAsync<ModelProcessException>(() => manager.StartAsync(failing.Launch, TestPipes.Timeout()));
        }

        var logs = capture.AllText;
        Assert.Contains("loaded its model", logs, StringComparison.Ordinal);
        Assert.Contains("serves 4 slots of 2048 tokens", logs, StringComparison.Ordinal);
        Assert.Contains("runs 14 threads", logs, StringComparison.Ordinal);
        Assert.Contains("evaluated 22 prompt tokens", logs, StringComparison.Ordinal);
        Assert.Contains("generated 16 tokens", logs, StringComparison.Ordinal);
        Assert.Contains("reported a warning (text withheld)", logs, StringComparison.Ordinal);
        Assert.Contains("reported an error (text withheld)", logs, StringComparison.Ordinal);
        Assert.Contains("could not load the model", logs, StringComparison.Ordinal);
        foreach (var secret in SampleMessages.PrivateStrings)
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("load_model", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(".sock", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(".gguf", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchAndOptions_KeepPathsOutOfTheirText()
    {
        var launch = new ModelProcessLaunch(@"C:\Users\PRIVATE-PATH-0c5a\model.gguf");
        var options = new ModelProcessOptions(@"C:\Users\PRIVATE-PATH-0c5a\sockets");

        Assert.DoesNotContain("PRIVATE-PATH-0c5a", launch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", new ModelRuntimeOptions(@"C:\PRIVATE-PATH-0c5a").ToString(), StringComparison.Ordinal);
        foreach (var failure in Enum.GetValues<ModelProcessFailure>())
        {
            Assert.DoesNotContain("\\", new ModelProcessException(failure).Message, StringComparison.Ordinal);
        }
    }
}

/// <summary>The real llama.cpp server bundled with the app, run the way the host runs it.</summary>
public sealed class BundledEngineTests
{
    [Fact]
    public async Task BundledServer_RunsHidden_AndReportsAModelItCannotLoad_WithoutLeakingItsPath()
    {
        var directory = TestRuntimes.NewTempDirectory();
        try
        {
            using var capture = new CapturingLoggerProvider();
            using var loggers = capture.CreateFactory();
            var model = Path.Combine(directory, "PRIVATE-PATH-0c5a", "missing.gguf");
            var sockets = Path.Combine(directory, "s");
            await using var manager = new ModelProcessManager(
                TestRuntimes.Bundled,
                new ModelProcessOptions(sockets),
                TimeProvider.System,
                loggers.CreateLogger<ModelProcessManager>());

            var exception = await Assert.ThrowsAsync<ModelProcessException>(
                () => manager.StartAsync(new ModelProcessLaunch(model), TestPipes.Timeout(60)));

            Assert.Equal(ModelProcessFailure.ModelLoadFailed, exception.Failure);
            Assert.Equal(ModelProcessState.Failed, manager.State);
            Assert.Empty(Directory.GetFiles(sockets));
            Assert.Contains("launched", capture.AllText, StringComparison.Ordinal);
            Assert.Contains("could not load the model", capture.AllText, StringComparison.Ordinal);
            Assert.Contains("did not start (ModelLoadFailed, exit code 1)", capture.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE-PATH-0c5a", capture.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("missing.gguf", capture.AllText, StringComparison.Ordinal);
        }
        finally
        {
            TestRuntimes.DeleteDirectory(directory);
        }
    }
}

/// <summary>The host's lifetime: the engine ends when the host stops serving.</summary>
public sealed class ModelHostServiceTests
{
    [Fact]
    public async Task ServingEnds_EndsTheEngine_BeforeTheHostStops()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var engine = setup.CreateManager();
        await engine.StartAsync(setup.Launch, TestPipes.Timeout());
        var processId = engine.ProcessId!.Value;

        // An owner that never connects, so serving ends on its own.
        var options = new ModelHostOptions(TestPipes.NewName(), null) { ConnectTimeout = TimeSpan.FromMilliseconds(100) };
        var server = new ModelHostServer(
            options,
            new ModelHostSession(
                TestHandlers.Create(new NullModelController()), NullLogger<ModelHostSession>.Instance),
            TimeProvider.System,
            NullLogger<ModelHostServer>.Instance);
        var lifetime = new RecordingLifetime(() => engine.State);
        using var service = new ModelHostService(server, engine, options, lifetime, NullLogger<ModelHostService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var stateWhenStopping = await lifetime.Stopping.WaitAsync(TestPipes.Timeout());
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(ModelProcessState.Stopped, stateWhenStopping);
        Assert.False(TestRuntimes.IsRunning(processId));
        Assert.Equal(1, service.ExitCode);
    }

    private sealed class RecordingLifetime(Func<ModelProcessState> engineState) : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource<ModelProcessState> _stopping =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProcessState> Stopping => _stopping.Task;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.TrySetResult(engineState());
    }
}
