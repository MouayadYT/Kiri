using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.Core.Startup;
using Assistant.UI.Tray;
using Assistant.Windows.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The menu behind the Assistant's icon in the notification area (PROJECT_SPEC §4.9, step 121): its lines, what each does, the Pause Local AI
/// toggle, and the sign-in entry that is kept pointing at the running copy. The icon is a stand-in; the real one is tested in the Windows tests.
/// </summary>
public sealed class TrayControllerTests
{
    [Fact]
    public void TheMenuHasTheFiveCommandsInOrder_WithOpenAssistantAsTheDefault()
    {
        using var kit = new Kit();

        var menu = kit.Icon.Menu;

        Assert.Equal(
            ["Open Assistant", "New Conversation", "", "Settings", "Pause Local AI", "", "Exit"],
            menu.Select(item => item.Text));
        Assert.Equal(["Open Assistant"], menu.Where(item => item.IsDefault).Select(item => item.Text));
        Assert.Equal([2, 5], Enumerable.Range(0, menu.Count).Where(index => menu[index].IsSeparator));
        Assert.Equal(5, menu.Where(item => !item.IsSeparator).Select(item => item.Id).Distinct().Count());
        Assert.All(menu, item => Assert.True(item.IsEnabled));
        Assert.Equal("Assistant", kit.Icon.Tooltip);
    }

    [Fact]
    public void AClickOnTheIconOpensTheAssistant()
    {
        using var kit = new Kit();

        kit.Icon.Select();

        Assert.Equal(["open"], kit.Actions);
    }

    [Fact]
    public void EachCommandDoesItsOwnThingOnce()
    {
        using var kit = new Kit();

        kit.Choose("Open Assistant");
        kit.Choose("New Conversation");
        kit.Choose("Settings");
        kit.Choose("Exit");

        Assert.Equal(["open", "new-conversation", "settings", "exit"], kit.Actions);
        Assert.Equal(0, kit.Pause.Pauses);
    }

    [Fact]
    public void PauseLocalAiPausesTheModel_AndTheMenuAndTooltipSayItIsPaused_UntilResumed()
    {
        using var kit = new Kit();

        kit.Choose("Pause Local AI");

        Assert.Equal(1, kit.Pause.Pauses);
        Assert.True(kit.Pause.IsPaused);
        Assert.Equal("Resume Local AI", kit.Icon.Menu[4].Text);
        Assert.Equal("Assistant (local AI paused)", kit.Icon.Tooltip);

        kit.Choose("Resume Local AI");

        Assert.Equal(1, kit.Pause.Resumes);
        Assert.False(kit.Pause.IsPaused);
        Assert.Equal("Pause Local AI", kit.Icon.Menu[4].Text);
        Assert.Equal("Assistant", kit.Icon.Tooltip);
        Assert.Equal(1, kit.Pause.Pauses);
    }

    [Fact]
    public void APauseAnnouncedFromAnotherThreadIsShownOnTheUiThread()
    {
        using var kit = new Kit();
        kit.Hold = true;

        kit.Pause.PauseFromAnotherThread();

        // Nothing changes on the icon until the UI thread runs what was posted to it.
        Assert.Equal("Pause Local AI", kit.Icon.Menu[4].Text);
        kit.Hold = false;
        kit.RunPosted();
        Assert.Equal("Resume Local AI", kit.Icon.Menu[4].Text);
    }

    [Fact]
    public void TheMenuIsShownTheWayTheIconWasToldWhenAPausePausedItFromSomewhereElse()
    {
        using var kit = new Kit();

        // Paused by something other than the menu: the menu still says so the next time it opens.
        kit.Pause.PauseDirectly();

        Assert.Equal("Resume Local AI", kit.Icon.Menu[4].Text);
        Assert.Equal("Assistant (local AI paused)", kit.Icon.Tooltip);
    }

    [Fact]
    public void WhenGameModePausedTheModel_TheTooltipSaysSo_AndResumeLocalAiStillResumesIt()
    {
        using var kit = new Kit();

        kit.Pause.PauseDirectly(LocalAiPauseReason.GameMode);

        Assert.Equal("Resume Local AI", kit.Icon.Menu[4].Text);
        Assert.Equal("Assistant (game mode: local AI paused)", kit.Icon.Tooltip);

        kit.Choose("Resume Local AI");

        Assert.False(kit.Pause.IsPaused);
        Assert.Equal("Assistant", kit.Icon.Tooltip);
    }

    [Fact]
    public void ACommandThatFailsIsLoggedAndTheNextOneStillWorks()
    {
        using var kit = new Kit { FailOpen = true };

        kit.Icon.Select();
        kit.Choose("Settings");

        Assert.Equal(["settings"], kit.Actions);
    }

    [Fact]
    public void AFailedPauseDoesNotEndTheApp()
    {
        using var kit = new Kit();
        kit.Pause.FailPause = true;

        kit.Choose("Pause Local AI");

        Assert.Equal(1, kit.Pause.Pauses);
        Assert.Empty(kit.Actions);
    }

    [Fact]
    public void StartPutsTheIconInTheNotificationAreaAndSaysWhetherWindowsTookIt()
    {
        using var kit = new Kit();

        Assert.True(kit.Controller.Start());
        Assert.True(kit.Icon.IsShown);

        kit.Icon.Refuse = true;
        kit.Icon.Hide();
        Assert.False(kit.Controller.Start());
    }

    [Fact]
    public void DisposingRemovesTheIconAndStopsListening()
    {
        var kit = new Kit();
        kit.Controller.Start();

        kit.Dispose();

        Assert.True(kit.Icon.Disposed);
        Assert.False(kit.Icon.IsShown);
        kit.Icon.Select();
        kit.Pause.PauseDirectly();
        Assert.Empty(kit.Actions);
    }

    // -- Starting with Windows: the sign-in entry that follows the running copy. --

    [Fact]
    public async Task TheSignInEntryIsRefreshedAtStartOnlyWhileTheSettingIsOn()
    {
        var launch = new RecordingLaunch();
        var settings = new InMemorySettingsService();
        var refresh = new LaunchAtLoginRefresh(settings, launch, NullLogger<LaunchAtLoginRefresh>.Instance);

        Assert.False(await refresh.RefreshAsync(CancellationToken.None));
        Assert.Equal(0, launch.Refreshes);

        await settings.SaveAsync(new AppSettings { LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true } });
        Assert.True(await refresh.RefreshAsync(CancellationToken.None));
        Assert.Equal(1, launch.Refreshes);
        Assert.Equal(0, launch.Enables);
    }

    [Fact]
    public async Task AFailedOrBrokenRefreshCostsOnlyTheEntry()
    {
        var launch = new RecordingLaunch { Works = false };
        var settings = new InMemorySettingsService();
        await settings.SaveAsync(new AppSettings { LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true } });
        var refresh = new LaunchAtLoginRefresh(settings, launch, NullLogger<LaunchAtLoginRefresh>.Instance);

        Assert.False(await refresh.RefreshAsync(CancellationToken.None));

        launch.Throws = true;
        Assert.False(await refresh.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheHostedServiceRefreshesWithoutWaitingForIt()
    {
        var launch = new RecordingLaunch();
        var settings = new InMemorySettingsService();
        await settings.SaveAsync(new AppSettings { LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true } });
        var refresh = new LaunchAtLoginRefresh(settings, launch, NullLogger<LaunchAtLoginRefresh>.Instance);

        await refresh.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (launch.Refreshes == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, launch.Refreshes);
        await refresh.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(@"C:\Apps\Assistant\Assistant.UI.exe", @"C:\Apps\Assistant\Assistant.UI.exe")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"D:\Out\Assistant.UI.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET", @"D:\Out\Assistant.UI.exe")]
    [InlineData(null, @"D:\Out\Assistant.UI.exe")]
    [InlineData("  ", @"D:\Out\Assistant.UI.exe")]
    public void TheEntryStartsTheAppsOwnExecutable_NeverTheDotnetHost(string? processPath, string expected)
    {
        Assert.Equal(expected, ApplicationPath.Choose(processPath, @"D:\Out" + Path.DirectorySeparatorChar));
    }

    private sealed class RecordingLaunch : ILaunchAtLogin
    {
        private int _refreshes;

        public bool Works { get; set; } = true;

        public bool Throws { get; set; }

        public int Refreshes => Volatile.Read(ref _refreshes);

        public int Enables { get; private set; }

        public LaunchAtLoginState GetState() => LaunchAtLoginState.On;

        public Task<bool> EnableAsync(CancellationToken cancellationToken = default)
        {
            Enables++;
            return Task.FromResult(Works);
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshes);
            return Throws ? throw new InvalidOperationException("The registry is not available.") : Task.FromResult(Works);
        }

        public Task<bool> DisableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Works);
    }

    private sealed class Kit : IDisposable
    {
        private readonly Queue<Action> _posted = new();

        public Kit()
        {
            Icon = new FakeIcon();
            Pause = new FakePause();
            Controller = new TrayController(
                Icon,
                open: () => Do("open", FailOpen),
                newConversation: () => Do("new-conversation"),
                openSettings: () => Do("settings"),
                Pause,
                exit: () => Do("exit"),
                post: action =>
                {
                    if (Hold)
                    {
                        _posted.Enqueue(action);
                    }
                    else
                    {
                        action();
                    }
                },
                NullLogger.Instance);
        }

        public FakeIcon Icon { get; }

        public FakePause Pause { get; }

        public TrayController Controller { get; }

        public List<string> Actions { get; } = [];

        public bool FailOpen { get; init; }

        public bool Hold { get; set; }

        public void Choose(string text) => Icon.Invoke(Icon.Menu.Single(item => item.Text == text).Id);

        public void RunPosted()
        {
            while (_posted.Count > 0)
            {
                _posted.Dequeue()();
            }
        }

        public void Dispose() => Controller.Dispose();

        private void Do(string name, bool fail = false)
        {
            if (fail)
            {
                throw new InvalidOperationException("A command that fails.");
            }

            Actions.Add(name);
        }
    }

    private sealed class FakeIcon : INotificationAreaIcon
    {
        public bool IsShown { get; private set; }

        public bool Refuse { get; set; }

        public bool Disposed { get; private set; }

        public string Tooltip { get; set; } = "";

        public IReadOnlyList<TrayMenuItem> Menu { get; set; } = [];

        public event EventHandler? Selected;

        public event EventHandler<int>? CommandInvoked;

        public bool Show()
        {
            IsShown = !Refuse;
            return IsShown;
        }

        public void Hide() => IsShown = false;

        public void Select() => Selected?.Invoke(this, EventArgs.Empty);

        public void Invoke(int id) => CommandInvoked?.Invoke(this, id);

        public void Dispose()
        {
            Disposed = true;
            IsShown = false;
        }
    }

    private sealed class FakePause : ILocalAiPause
    {
        public bool IsPaused => Reason != LocalAiPauseReason.None;

        public LocalAiPauseReason Reason { get; private set; }

        public bool FailPause { get; set; }

        public int Pauses { get; private set; }

        public int Resumes { get; private set; }

        public event EventHandler? Changed;

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            Pauses++;
            if (FailPause)
            {
                throw new ModelHostException("The model host is not there.");
            }

            PauseDirectly();
            return Task.CompletedTask;
        }

        public void Resume()
        {
            Resumes++;
            Reason = LocalAiPauseReason.None;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void PauseDirectly() => PauseDirectly(LocalAiPauseReason.User);

        public void PauseDirectly(LocalAiPauseReason reason)
        {
            Reason = reason;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void PauseFromAnotherThread() => Task.Run(PauseDirectly).GetAwaiter().GetResult();
    }
}
