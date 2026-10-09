using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// Pause Local AI from the tray menu (PROJECT_SPEC §4.9, step 121), through the real lifecycle over a real session and the fake engine: the
/// model is unloaded, nothing loads it while the user has paused, and resuming lets the next question load it again.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    [Fact]
    public async Task Pause_UnloadsTheModel_AndNothingLoadsItUntilTheUserResumes()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        var changes = 0;
        lifecycle.Changed += (_, _) => changes++;
        var files = new ModelFiles(setup.ScenarioPath);
        await lifecycle.LoadAsync(files, TestPipes.Timeout());
        Assert.NotNull(lifecycle.Model);
        Assert.False(lifecycle.IsPaused);

        await lifecycle.PauseAsync(TestPipes.Timeout());

        Assert.True(lifecycle.IsPaused);
        Assert.Equal(1, changes);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        Assert.Null(lifecycle.Model);

        // A question asked while paused is not answered, and says why; it does not load the model.
        var refused = await Assert.ThrowsAsync<ModelHostException>(() => lifecycle.LoadAsync(files, TestPipes.Timeout()));
        Assert.Equal(ModelHostErrorCode.Paused, refused.Code);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        Assert.Equal(ModelErrorText.PausedText, ModelErrorText.Describe(refused));

        lifecycle.Resume();

        // Resuming loads nothing by itself: the next question does, as at the start.
        Assert.False(lifecycle.IsPaused);
        Assert.Equal(2, changes);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        await lifecycle.LoadAsync(files, TestPipes.Timeout());
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
        await statuses.WaitForAsync(ModelStatus.Ready);
        Assert.Equal(1, host.Launcher.Started);
    }

    [Fact]
    public async Task Pause_WithNoModelLoaded_StartsNoHost_AndAPausedLoadDoesNotStartOne()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        await lifecycle.PauseAsync(TestPipes.Timeout());
        var refused = await Assert.ThrowsAsync<ModelHostException>(() =>
            lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.Paused, refused.Code);
        Assert.Equal(0, host.Launcher.Started);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
    }

    [Fact]
    public async Task PausingOrResumingTwice_ChangesNothingTheSecondTime()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var changes = 0;
        lifecycle.Changed += (_, _) => changes++;

        lifecycle.Resume();
        await lifecycle.PauseAsync(TestPipes.Timeout());
        await lifecycle.PauseAsync(TestPipes.Timeout());
        lifecycle.Resume();
        lifecycle.Resume();

        Assert.Equal(2, changes);
        Assert.False(lifecycle.IsPaused);
    }

    [Fact]
    public async Task APauseByGameMode_UnloadsTheModelTheSameWay_AndSaysItWasTheGame()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        ILocalAiPause pause = lifecycle;
        var files = new ModelFiles(setup.ScenarioPath);
        await lifecycle.LoadAsync(files, TestPipes.Timeout());
        Assert.Equal(LocalAiPauseReason.None, pause.Reason);

        await pause.PauseAsync(LocalAiPauseReason.GameMode, TestPipes.Timeout());

        Assert.True(pause.IsPaused);
        Assert.Equal(LocalAiPauseReason.GameMode, pause.Reason);
        Assert.Equal(ModelStatus.NotLoaded, lifecycle.Current.Status);
        Assert.Null(lifecycle.Model);

        // A question asked meanwhile is told why, and how to get the local AI back now; it loads nothing.
        var refused = await Assert.ThrowsAsync<ModelHostException>(() => lifecycle.LoadAsync(files, TestPipes.Timeout()));
        Assert.Equal(ModelHostErrorCode.PausedForGame, refused.Code);
        Assert.Equal(ModelErrorText.PausedForGameText, ModelErrorText.Describe(refused));

        // The user's own Pause Local AI changes nothing while it is paused already: it stays game mode's pause, which ends with the game.
        await pause.PauseAsync(TestPipes.Timeout());
        Assert.Equal(LocalAiPauseReason.GameMode, pause.Reason);

        pause.Resume();

        Assert.Equal(LocalAiPauseReason.None, pause.Reason);
        await lifecycle.LoadAsync(files, TestPipes.Timeout());
        Assert.Equal(ModelStatus.Ready, lifecycle.Current.Status);
    }

    [Fact]
    public async Task APauseNeedsAReason()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lifecycle.PauseAsync(LocalAiPauseReason.None, TestPipes.Timeout()));
        Assert.False(lifecycle.IsPaused);
    }

    [Fact]
    public async Task Pause_WhileTheModelLoads_EndsTheLoad_WithThePausedReason_AndLeavesNothingLoaded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { ReadyDelayMs = 3000 });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);

        var loading = lifecycle.LoadAsync(new ModelFiles(setup.ScenarioPath), TestPipes.Timeout());
        await statuses.WaitForAsync(ModelStatus.Loading);
        await WaitUntilAsync(() => host.Launcher.Started == 1);
        await lifecycle.PauseAsync(TestPipes.Timeout());

        var ended = await Assert.ThrowsAsync<ModelHostException>(() => loading);
        Assert.Equal(ModelHostErrorCode.Paused, ended.Code);
        await WaitUntilAsync(() => lifecycle.Current.Status == ModelStatus.NotLoaded);
        Assert.Null(lifecycle.Model);
    }
}
