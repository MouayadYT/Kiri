using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.UI.Search;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- What the bar's quick actions do (PROJECT_SPEC §4.1): only the safe, built ones, and only what is asked of them. ----

    private sealed class RecordingSystem : ISystemActions
    {
        public List<string> Calls { get; } = [];

        public bool Works { get; set; } = true;

        public int? Volume { get; set; } = 50;

        public Dictionary<SystemFolder, string> Folders { get; } = new()
        {
            [SystemFolder.Downloads] = @"C:\Users\someone\Downloads",
            [SystemFolder.Home] = @"C:\Users\someone",
        };

        public bool OpenWindowsSettings() => Record("settings");

        public bool LockWorkstation() => Record("lock");

        public bool SetMuted(bool muted) => Record(muted ? "mute" : "unmute");

        public bool SetVolume(int percent) => Record("volume " + percent);

        public int? ChangeVolume(int percent)
        {
            Record("change " + percent);
            return Works ? Volume : null;
        }

        public VolumeState? GetVolume() => Works && Volume is { } percent ? new VolumeState(percent, false) : null;

        public string? GetFolder(SystemFolder folder) => Folders.GetValueOrDefault(folder);

        private bool Record(string call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }

            return Works;
        }
    }

    private sealed class RecordingAssistant : IAssistantCommands
    {
        public List<string> Calls { get; } = [];

        public void NewConversation() => Calls.Add("new");

        public void ShowHistory() => Calls.Add("history");

        public void OpenSettings() => Calls.Add("settings");

        public void TakeScreenshot() => Calls.Add("screenshot");
    }

    private static (QuickActionExecutor Executor, RecordingSystem System, RecordingAssistant Assistant, RecordingLauncher Files, ClipboardHistory History)
        CreateExecutor(IQuickActionCatalog? catalog = null, bool history = true)
    {
        var system = new RecordingSystem();
        var assistant = new RecordingAssistant();
        var files = new RecordingLauncher();
        var clipboard = new ClipboardHistory(clock: new FixedClock(QuickNow));
        var executor = new QuickActionExecutor(catalog ?? QuickActionCatalog.Default, system, files, assistant, history ? clipboard : null);
        return (executor, system, assistant, files, clipboard);
    }

    [Fact]
    public async Task EveryBuiltSafeActionIsWiredAndNothingElseCanRun()
    {
        var (executor, _, _, _, _) = CreateExecutor();

        foreach (var definition in QuickActionCatalog.Default.All)
        {
            Assert.Equal(definition.IsSafeToRun, executor.CanRun(definition.Id));
        }

        Assert.False(executor.CanRun("no.such.action"));
        Assert.False(executor.CanRun(null!));
        var refused = await executor.RunAsync(QuickActionIds.ShutDown, null);
        Assert.False(refused.Succeeded);
        Assert.False((await executor.RunAsync(QuickActionIds.EmptyRecycleBin, null)).Succeeded);
        Assert.False((await executor.RunAsync(QuickActionIds.Sleep, null)).Succeeded);
        Assert.False((await executor.RunAsync("anything; format c:", null)).Succeeded);
    }

    [Fact]
    public async Task TheAssistantsOwnActionsAreHandedToTheAssistant()
    {
        var (executor, _, assistant, _, _) = CreateExecutor();

        foreach (var id in new[]
        {
            QuickActionIds.NewConversation, QuickActionIds.ShowHistory, QuickActionIds.OpenAssistantSettings, QuickActionIds.TakeScreenshot,
        })
        {
            Assert.True((await executor.RunAsync(id, null)).Succeeded);
        }

        Assert.Equal(["new", "history", "settings", "screenshot"], assistant.Calls);
    }

    [Fact]
    public async Task WindowsActionsAreTheClosedSetOfSystemActions()
    {
        var (executor, system, _, _, _) = CreateExecutor();

        Assert.True((await executor.RunAsync(QuickActionIds.OpenWindowsSettings, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.Mute, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.Unmute, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.VolumeUp, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.VolumeDown, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.LockPc, null)).Succeeded);

        Assert.Equal(["settings", "mute", "unmute", "change 10", "change -10", "lock"], system.Calls);
    }

    [Fact]
    public async Task AFailureIsAnOutcomeThatSaysWhatCouldNotBeDone()
    {
        var (executor, system, _, _, _) = CreateExecutor();
        system.Works = false;

        var lockFailed = await executor.RunAsync(QuickActionIds.LockPc, null);
        var volumeFailed = await executor.RunAsync(QuickActionIds.VolumeUp, null);

        Assert.False(lockFailed.Succeeded);
        Assert.Equal("The PC could not be locked.", lockFailed.Message);
        Assert.Equal("The volume could not be changed.", volumeFailed.Message);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("30", true)]
    [InlineData("100", true)]
    [InlineData("101", false)]
    [InlineData("-5", false)]
    [InlineData("30.5", false)]
    [InlineData("thirty", false)]
    [InlineData(" 30", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task OnlyAWholeNumberWithinTheActionsRangeSetsTheVolume(string? argument, bool allowed)
    {
        var (executor, system, _, _, _) = CreateExecutor();

        var outcome = await executor.RunAsync(QuickActionIds.SetVolume, argument);

        Assert.Equal(allowed, outcome.Succeeded);
        Assert.Equal(allowed ? [$"volume {argument}"] : [], system.Calls);
    }

    [Fact]
    public async Task AnActionThatTakesNoNumberIsGivenNone()
    {
        var (executor, system, _, _, _) = CreateExecutor();

        var outcome = await executor.RunAsync(QuickActionIds.Mute, "100");

        Assert.False(outcome.Succeeded);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task AFolderIsOpenedByThePathWindowsGivesForIt()
    {
        var (executor, system, _, files, _) = CreateExecutor();

        Assert.True((await executor.RunAsync(QuickActionIds.OpenDownloads, null)).Succeeded);
        Assert.True((await executor.RunAsync(QuickActionIds.OpenHome, null)).Succeeded);
        var missing = await executor.RunAsync(QuickActionIds.OpenMusic, null);

        Assert.Equal([@"C:\Users\someone\Downloads", @"C:\Users\someone"], files.Opened);
        Assert.False(missing.Succeeded);
        Assert.Equal("Windows does not have that folder.", missing.Message);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task ClearingTheClipboardHistoryForgetsEverythingAndIsOnlyThereWithAHistory()
    {
        var (executor, _, _, _, history) = CreateExecutor();
        history.SetEnabled(true);
        history.Add("copied");

        Assert.True((await executor.RunAsync(QuickActionIds.ClearClipboardHistory, null)).Succeeded);

        Assert.Empty(history.Items);
        Assert.False(CreateExecutor(history: false).Executor.CanRun(QuickActionIds.ClearClipboardHistory));
    }

    [Fact]
    public async Task AnExceptionFromWhatDoesTheActionIsAFailureAndAnActionTheCatalogDoesNotAllowIsNeverRun()
    {
        var catalog = new QuickActionCatalog(
        [
            .. QuickActionCatalog.Default.All.Where(definition => definition.Id != QuickActionIds.LockPc),
            QuickActionCatalog.Default.Find(QuickActionIds.LockPc)! with { Availability = QuickActionAvailability.Planned },
        ]);
        var (executor, system, _, _, _) = CreateExecutor(catalog);

        // The action is wired, but the catalog says it is not available: it is not run.
        Assert.False(executor.CanRun(QuickActionIds.LockPc));
        Assert.False((await executor.RunAsync(QuickActionIds.LockPc, null)).Succeeded);
        Assert.Empty(system.Calls);

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RunAsync(QuickActionIds.Mute, null, cancel.Token));
    }
}
