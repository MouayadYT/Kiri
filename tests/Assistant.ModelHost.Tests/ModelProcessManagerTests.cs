using System.Diagnostics;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The engine as a managed child process, played by the fake engine.</summary>
public sealed class ModelProcessManagerTests
{
    [Fact]
    public async Task Start_LaunchesTheEngineHidden_AndWaitsUntilItListens()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { ReadyDelayMs = 200 });
        await using var manager = setup.CreateManager();

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());

        Assert.Equal(ModelProcessState.Running, manager.State);
        var report = setup.Report(1);
        Assert.Equal(report.ProcessId, manager.ProcessId);
        Assert.False(report.HasConsoleWindow);
        using (var process = Process.GetProcessById(report.ProcessId))
        {
            Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
        }

        Assert.Equal(
            ["--model", setup.ScenarioPath, "--host", manager.SocketPath!, "--offline", "--no-ui", "--no-slots",
                "--log-verbosity", "3", "--log-colors", "off", "--parallel", "1", "--ctx-size", "8000", "--no-mmproj-auto"],
            report.Arguments);
        Assert.Equal(setup.SocketDirectory, Path.GetDirectoryName(manager.SocketPath));
        Assert.EndsWith(".sock", manager.SocketPath, StringComparison.Ordinal);
        Assert.True(File.Exists(manager.SocketPath));
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.TrimEndingDirectorySeparator(report.WorkingDirectory),
            ignoreCase: true);
    }

    [Fact]
    public async Task Start_KeepsLlamaSettingsInTheEnvironment_AwayFromTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        string[] variables = ["LLAMA_ARG_HOST", "LLAMA_API_KEY", "HF_TOKEN", "GGML_BACKEND_PATH"];
        foreach (var variable in variables)
        {
            Environment.SetEnvironmentVariable(variable, "0.0.0.0");
        }

        try
        {
            await using var manager = setup.CreateManager();
            await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        }
        finally
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        Assert.Empty(setup.Report(1).LlamaVariables);
    }

    [Fact]
    public async Task UnexpectedExit_RestartsTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = 2 });
        await using var manager = setup.CreateManager();
        using var states = new StateRecorder(manager);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        await states.WaitForAsync(ModelProcessState.Running, times: 3);

        Assert.Equal(
            [ModelProcessState.Starting, ModelProcessState.Running, ModelProcessState.Restarting,
                ModelProcessState.Running, ModelProcessState.Restarting, ModelProcessState.Running],
            states.States);
        Assert.Equal(3, setup.Launches);
        Assert.Equal(setup.Report(3).ProcessId, manager.ProcessId);
        Assert.False(TestRuntimes.IsRunning(setup.Report(1).ProcessId));
        Assert.Single(Directory.GetFiles(setup.SocketDirectory));
    }

    [Fact]
    public async Task EngineThatKeepsExiting_IsLeftFailed_AfterTheRestartLimit()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = -1 });
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory(privacyFilter: true);
        await using var manager = setup.CreateManager(setup.Options(maxRestarts: 2), loggers);
        using var states = new StateRecorder(manager);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        await states.WaitForAsync(ModelProcessState.Failed);
        await Task.Delay(300);

        Assert.Equal(ModelProcessFailure.RestartLimitReached, manager.Failure);
        Assert.Equal(ModelProcessState.Failed, manager.State);
        Assert.Null(manager.ProcessId);
        Assert.Equal(3, setup.Launches);
        Assert.Contains("Restarting the model engine in 20 ms (restart 1 of 2)", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Restarting the model engine in 40 ms (restart 2 of 2)", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("exited unexpectedly with code 3", capture.AllText, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(setup.SocketDirectory));
    }

    [Fact]
    public async Task RestartsThatFailToStart_CountTowardTheLimit()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario
        {
            CrashingLaunches = 1,
            StartupFailure = "model",
            StartupFailureFromLaunch = 2,
        });
        await using var manager = setup.CreateManager(setup.Options(maxRestarts: 2));
        using var states = new StateRecorder(manager);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        await states.WaitForAsync(ModelProcessState.Failed);

        Assert.Equal(ModelProcessFailure.RestartLimitReached, manager.Failure);
        Assert.Equal(3, setup.Launches);
    }

    [Fact]
    public async Task RunThatLastedLongEnough_StartsTheRestartCountOver()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = -1, CrashAfterMs = 250 });
        var options = setup.Options(maxRestarts: 1) with
        {
            RestartPolicy = setup.Options().RestartPolicy with { MaxRestarts = 1, StableUptime = TimeSpan.FromMilliseconds(100) },
        };
        await using var manager = setup.CreateManager(options);
        using var states = new StateRecorder(manager);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        await states.WaitForAsync(ModelProcessState.Running, times: 4);

        Assert.DoesNotContain(ModelProcessState.Failed, states.States);
        await manager.StopAsync(TestPipes.Timeout());
        Assert.Equal(ModelProcessState.Stopped, manager.State);
    }

    [Theory]
    [InlineData("model", nameof(ModelProcessFailure.ModelLoadFailed))]
    [InlineData("bind", nameof(ModelProcessFailure.EndpointUnavailable))]
    public async Task EngineThatReportsAFailureAtStart_FailsTheStart_WithoutRestarting(
        string startupFailure,
        string failure)
    {
        var expected = Enum.Parse<ModelProcessFailure>(failure);
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { StartupFailure = startupFailure });
        await using var manager = setup.CreateManager();

        var exception = await Assert.ThrowsAsync<ModelProcessException>(
            () => manager.StartAsync(setup.Launch, TestPipes.Timeout()));
        await Task.Delay(300);

        Assert.Equal(expected, exception.Failure);
        Assert.Equal(ModelProcessState.Failed, manager.State);
        Assert.Equal(expected, manager.Failure);
        Assert.Equal(1, setup.Launches);
        Assert.Empty(Directory.GetFiles(setup.SocketDirectory));
    }

    [Fact]
    public async Task EngineWhoseDllsDoNotLoad_IsNotRestarted()
    {
        const int dllNotFound = unchecked((int)0xC0000135);
        using var starting = FakeEngineSetup.Create(new FakeEngineScenario { StartupExitCode = dllNotFound });
        using var running = FakeEngineSetup.Create(new FakeEngineScenario { CrashingLaunches = 1, CrashExitCode = dllNotFound });
        await using var first = starting.CreateManager();
        await using var second = running.CreateManager();
        using var states = new StateRecorder(second);

        var exception = await Assert.ThrowsAsync<ModelProcessException>(
            () => first.StartAsync(starting.Launch, TestPipes.Timeout()));
        await second.StartAsync(running.Launch, TestPipes.Timeout());
        await states.WaitForAsync(ModelProcessState.Failed);

        Assert.Equal(ModelProcessFailure.NativeLoadFailed, exception.Failure);
        Assert.Equal(ModelProcessFailure.NativeLoadFailed, second.Failure);
        Assert.Equal(1, starting.Launches);
        Assert.Equal(1, running.Launches);
    }

    [Fact]
    public async Task EngineThatNeverListens_TimesOut_AndIsEnded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var manager = setup.CreateManager(setup.Options() with { StartTimeout = TimeSpan.FromMilliseconds(700) });

        var exception = await Assert.ThrowsAsync<ModelProcessException>(
            () => manager.StartAsync(setup.Launch, TestPipes.Timeout()));

        Assert.Equal(ModelProcessFailure.StartTimedOut, exception.Failure);
        Assert.False(TestRuntimes.IsRunning(setup.Report(1).ProcessId));
        Assert.Empty(Directory.GetFiles(setup.SocketDirectory));
    }

    [Fact]
    public async Task RuntimeThatCannotRun_IsNotLaunched()
    {
        var locator = new StubRuntimeLocator(ModelRuntimeStatus.Unavailable(ModelRuntimeState.Incomplete, ["llama.dll"]));
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var manager = new ModelProcessManager(
            locator, setup.Options(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelProcessManager>.Instance);

        var exception = await Assert.ThrowsAsync<ModelProcessException>(
            () => manager.StartAsync(setup.Launch, TestPipes.Timeout()));

        Assert.Equal(ModelProcessFailure.RuntimeUnavailable, exception.Failure);
        Assert.Equal(ModelRuntimeState.Incomplete, exception.RuntimeState);
        Assert.Equal(0, setup.Launches);
    }

    [Fact]
    public async Task SocketPathTooLongForWindows_IsRefusedBeforeLaunching()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var options = setup.Options() with { SocketDirectory = Path.Combine(setup.Directory, new string('s', 100)) };
        await using var manager = setup.CreateManager(options);

        var exception = await Assert.ThrowsAsync<ModelProcessException>(
            () => manager.StartAsync(setup.Launch, TestPipes.Timeout()));

        Assert.Equal(ModelProcessFailure.EndpointUnavailable, exception.Failure);
        Assert.Equal(0, setup.Launches);
    }

    [Fact]
    public async Task Stop_EndsTheEngine_RemovesItsSocket_AndDoesNotRestartIt()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory(privacyFilter: true);
        await using var manager = setup.CreateManager(loggers: loggers);
        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        var processId = manager.ProcessId!.Value;
        var socket = manager.SocketPath!;

        await manager.StopAsync(TestPipes.Timeout());
        await Task.Delay(300);

        Assert.Equal(ModelProcessState.Stopped, manager.State);
        Assert.Null(manager.ProcessId);
        Assert.Null(manager.SocketPath);
        Assert.False(TestRuntimes.IsRunning(processId));
        Assert.False(File.Exists(socket));
        Assert.Equal(1, setup.Launches);
        Assert.Contains($"Model engine process {processId} stopped", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("exited unexpectedly", capture.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ShowsStopping_WhileTheEngineExits_ThenStopped()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var manager = setup.CreateManager();
        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        using var states = new StateRecorder(manager);

        await manager.StopAsync(TestPipes.Timeout());
        await manager.StopAsync(TestPipes.Timeout());

        // Stopping an engine that is not running changes nothing.
        Assert.Equal([ModelProcessState.Stopping, ModelProcessState.Stopped], states.States);
    }

    [Fact]
    public async Task ContextLength_IsWhatTheEngineReported_WhileItRuns()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var manager = setup.CreateManager();
        Assert.Null(manager.ContextLength);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        Assert.Equal(2048, manager.ContextLength);

        await manager.StopAsync(TestPipes.Timeout());
        Assert.Null(manager.ContextLength);
    }

    [Fact]
    public async Task Stop_WhileStarting_CancelsTheStart_AndEndsTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { NeverReady = true });
        await using var manager = setup.CreateManager();
        var starting = manager.StartAsync(setup.Launch, TestPipes.Timeout());
        while (!File.Exists(FakeEngineReport.PathOf(setup.ScenarioPath, 1)))
        {
            await Task.Delay(20, TestPipes.Timeout());
        }

        await manager.StopAsync(TestPipes.Timeout());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(ModelProcessState.Stopped, manager.State);
        await TestRuntimes.WaitUntilExitedAsync(setup.Report(1).ProcessId);
    }

    [Fact]
    public async Task StartingAgain_ReplacesTheRunningEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var manager = setup.CreateManager();
        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        var first = manager.ProcessId!.Value;

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());

        Assert.False(TestRuntimes.IsRunning(first));
        Assert.Equal(setup.Report(2).ProcessId, manager.ProcessId);
        Assert.Equal(ModelProcessState.Running, manager.State);
    }

    [Fact]
    public async Task Dispose_EndsTheEngine()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var manager = setup.CreateManager();
        await manager.StartAsync(setup.Launch, TestPipes.Timeout());
        var processId = manager.ProcessId!.Value;

        manager.Dispose();

        await TestRuntimes.WaitUntilExitedAsync(processId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(setup.Launch));
    }

    [Fact]
    public async Task ClosingTheJob_EndsTheProcessesInIt()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var startInfo = LlamaServerCommand.Create(
            setup.Locator.Locate().Runtime!, setup.Launch, Path.Combine(setup.Directory, "job.sock"));
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        startInfo.RedirectStandardInput = false;
        startInfo.StandardOutputEncoding = null;
        startInfo.StandardErrorEncoding = null;
        using var process = Process.Start(startInfo)!;
        var job = ProcessJob.Create();
        job.Assign(process);
        await Task.Delay(300);
        Assert.False(process.HasExited);

        job.Dispose();

        await process.WaitForExitAsync(TestPipes.Timeout());
    }
}
