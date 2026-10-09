using System.IO;
using Assistant.Core.Events;
using Assistant.Core.Gaming;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Gaming;
using Assistant.UI.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Game mode as the app runs it: what is let go of when a game is found, what is put back when it ends, and who wins when the user and a game
/// disagree. The detector, the local model, the voice and Handy are stand-ins, and the clock is moved by hand.
/// </summary>
public sealed class GameModeControllerTests
{
    private static readonly AppSettings On = new() { GameMode = new GameModeSettings { Games = true } };

    private static readonly AppSettings OnWithHandy = On with
    {
        Voice = new VoiceSettings { VoiceInputEnabled = true, SpeechRecognitionModelId = "handy" },
    };

    private static readonly RunningGame Game = new(20, "Cyberpunk 2077", GameEvidence.GamesFolder | GameEvidence.Fullscreen);

    [Fact]
    public async Task WhileGameModeIsOff_NothingWatchesForGames()
    {
        await using var kit = await Kit.StartAsync(new AppSettings());

        Assert.Equal(0, kit.Detector.Starts);
        Assert.Equal(GameModeStatus.Off, kit.Controller.Status);
        Assert.False(kit.Pause.IsPaused);
        Assert.False(kit.Voice.IsSuspended);
    }

    [Fact]
    public async Task AGame_ReleasesEveryModel_AndTheyComeBackAWhileAfterItEnds()
    {
        await using var kit = await Kit.StartAsync(On);
        Assert.Equal(1, kit.Detector.Starts);

        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);

        Assert.Equal(LocalAiPauseReason.GameMode, kit.Pause.Reason);
        Assert.True(kit.Voice.IsSuspended);
        Assert.Equal(new GameModeStatus(true, true, "Cyberpunk 2077", false), kit.Controller.Status);

        // The game ends. Nothing comes back at once: a game that ends is often about to start again.
        kit.Detector.End();
        kit.Clock.Advance(GameModeController.ResumeAfter - TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.True(kit.Controller.Status.IsActive);
        Assert.True(kit.Pause.IsPaused);

        kit.Clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => !kit.Controller.Status.IsActive);

        Assert.False(kit.Pause.IsPaused);
        Assert.False(kit.Voice.IsSuspended);
        Assert.Equal(new GameModeStatus(true, false, null, false), kit.Controller.Status);
    }

    [Fact]
    public async Task AGameThatStartsAgainAtOnce_KeepsTheModelsReleased()
    {
        await using var kit = await Kit.StartAsync(On);
        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);

        // A launcher hands over to the game: one process ends and another is found a moment later.
        kit.Detector.End();
        kit.Clock.Advance(TimeSpan.FromSeconds(3));
        kit.Detector.Find(Game with { ProcessId = 21 });
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);

        Assert.True(kit.Controller.Status.IsActive);
        Assert.Equal(1, kit.Voice.Suspends);
        Assert.Equal(0, kit.Voice.Resumes);
        Assert.Equal(0, kit.Pause.Resumes);
    }

    [Fact]
    public async Task AModelTheUserPausedThemselves_StaysPausedAfterTheGame()
    {
        await using var kit = await Kit.StartAsync(On);
        await kit.Pause.PauseAsync();

        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);
        Assert.Equal(LocalAiPauseReason.User, kit.Pause.Reason);
        Assert.True(kit.Voice.IsSuspended);

        kit.Detector.End();
        kit.Clock.Advance(GameModeController.ResumeAfter);
        await Until(() => !kit.Controller.Status.IsActive);

        // The voice comes back; the pause was the user's, and is theirs to end.
        Assert.False(kit.Voice.IsSuspended);
        Assert.Equal(LocalAiPauseReason.User, kit.Pause.Reason);
    }

    [Fact]
    public async Task ResumeLocalAiDuringAGame_WinsUntilThatGameEnds_AndTheNextGameIsAGameAgain()
    {
        await using var kit = await Kit.StartAsync(On);
        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);

        // The tray menu's Resume Local AI.
        kit.Pause.Resume();
        await Until(() => kit.Controller.Status.Overridden);

        Assert.False(kit.Voice.IsSuspended);
        Assert.False(kit.Pause.IsPaused);
        Assert.Equal(new GameModeStatus(true, false, "Cyberpunk 2077", true), kit.Controller.Status);

        // The same game goes on, and another window of it is found: the user's choice holds.
        kit.Detector.Find(Game);
        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.False(kit.Pause.IsPaused);
        Assert.Equal(1, kit.Voice.Suspends);

        kit.Detector.End();
        kit.Clock.Advance(GameModeController.ResumeAfter);
        await Until(() => !kit.Controller.Status.Overridden);

        kit.Detector.Find(Game with { ProcessId = 30, Name = "Another game" });
        await Until(() => kit.Controller.Status.IsActive);
        Assert.Equal(LocalAiPauseReason.GameMode, kit.Pause.Reason);
        Assert.Equal(2, kit.Voice.Suspends);
    }

    [Fact]
    public async Task HandyHoldingAModel_IsClosedForTheGame_AndOpenedAgainAfterwards()
    {
        await using var kit = await Kit.StartAsync(OnWithHandy);
        kit.Handy.HoldsModel = true;

        kit.Detector.Find(Game);
        await Until(() => kit.Handy.Closes == 1);
        Assert.False(kit.Handy.Running);
        Assert.True(File.Exists(kit.HandyMarker));

        kit.Detector.End();
        kit.Clock.Advance(GameModeController.ResumeAfter);
        await Until(() => kit.Handy.Starts == 1 && !File.Exists(kit.HandyMarker));

        Assert.True(kit.Handy.Running);
    }

    [Fact]
    public async Task HandyThatHasAlreadyLetGoOfItsModel_IsLeftAlone()
    {
        await using var kit = await Kit.StartAsync(OnWithHandy);
        kit.Handy.HoldsModel = false;

        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);
        kit.Detector.End();
        kit.Clock.Advance(GameModeController.ResumeAfter);
        await Until(() => !kit.Controller.Status.IsActive);

        Assert.Equal(0, kit.Handy.Closes);
        Assert.Equal(0, kit.Handy.Starts);
        Assert.True(kit.Handy.Running);
    }

    [Theory]
    [InlineData("handy", true, false)] // the user turned that part off
    [InlineData("asr-parakeet-v3", true, true)] // the Assistant has a recognizer of its own
    [InlineData("handy", false, true)] // voice control is off, so the Assistant does not use Handy at all
    public async Task HandyIsOnlyClosed_WhenTheAssistantSharesItsRecognizer_AndTheUserAllowsIt(string recognizer, bool voiceControl, bool release)
    {
        var settings = On with
        {
            GameMode = new GameModeSettings { Games = true, ReleaseSharedRecognizer = release },
            Voice = new VoiceSettings { VoiceInputEnabled = voiceControl, SpeechRecognitionModelId = recognizer },
        };
        await using var kit = await Kit.StartAsync(settings);
        kit.Handy.HoldsModel = true;

        kit.Detector.Find(Game);
        await Until(() => kit.Controller.Status.IsActive);

        Assert.Equal(0, kit.Handy.Closes);
        Assert.True(kit.Handy.Running);
    }

    [Fact]
    public async Task TurningGameModeOffDuringAGame_PutsEverythingBack_AndStopsWatching()
    {
        await using var kit = await Kit.StartAsync(OnWithHandy);
        kit.Handy.HoldsModel = true;
        kit.Detector.Find(Game);
        await Until(() => kit.Handy.Closes == 1 && kit.Controller.Status.IsActive);

        await kit.SaveAsync(settings => settings with { GameMode = settings.GameMode with { Games = false } });
        await Until(() => !kit.Controller.Status.Enabled);

        Assert.Equal(1, kit.Detector.Stops);
        Assert.False(kit.Pause.IsPaused);
        Assert.False(kit.Voice.IsSuspended);
        Assert.Equal(1, kit.Handy.Starts);
        Assert.Equal(GameModeStatus.Off, kit.Controller.Status);

        // And on again: the game that is still running is found again.
        await kit.SaveAsync(settings => settings with { GameMode = settings.GameMode with { Games = true } });
        await Until(() => kit.Controller.Status.IsActive);
        Assert.Equal(2, kit.Detector.Starts);
    }

    [Fact]
    public async Task TheDetectorIsToldWhatToLookFor_AndStartsOverWhenThatChanges()
    {
        // Creative apps alone: game mode is on, and games are not looked for.
        await using var kit = await Kit.StartAsync(new AppSettings { GameMode = new GameModeSettings { CreativeApps = true } });
        Assert.Equal(1, kit.Detector.Starts);
        Assert.False(kit.Detector.WatchGames);
        Assert.True(kit.Detector.WatchCreativeApps);
        Assert.True(kit.Controller.Status.Enabled);

        kit.Detector.Find(new RunningGame(40, "Adobe Premiere Pro 2025", GameEvidence.CreativeApp));
        await Until(() => kit.Controller.Status.IsActive);
        Assert.Equal(LocalAiPauseReason.GameMode, kit.Pause.Reason);
        Assert.True(kit.Voice.IsSuspended);
        Assert.Equal("Adobe Premiere Pro 2025", kit.Controller.Status.Game);

        // Games too: the detector is started over with both, and what is still running is found again, so nothing comes back in between.
        await kit.SaveAsync(settings => settings with { GameMode = settings.GameMode with { Games = true } });
        await Until(() => kit.Detector.Starts == 2);
        Assert.True(kit.Detector.WatchGames);
        Assert.True(kit.Detector.WatchCreativeApps);
        Assert.Equal(1, kit.Detector.Stops);
        await Task.Delay(100);
        Assert.True(kit.Controller.Status.IsActive);
        Assert.Equal(1, kit.Voice.Suspends);
        Assert.Equal(0, kit.Voice.Resumes);

        // A setting that has nothing to do with what is looked for leaves the detector running as it is.
        await kit.SaveAsync(settings => settings with { GameMode = settings.GameMode with { ReleaseSharedRecognizer = false } });
        await Task.Delay(100);
        Assert.Equal(2, kit.Detector.Starts);

        // Both off: nothing watches, and everything is back.
        await kit.SaveAsync(settings => settings with { GameMode = new GameModeSettings() });
        await Until(() => !kit.Controller.Status.Enabled);
        Assert.Equal(2, kit.Detector.Stops);
        Assert.False(kit.Pause.IsPaused);
        Assert.False(kit.Voice.IsSuspended);
    }

    [Fact]
    public async Task ClosingTheAssistantDuringAGame_OpensHandyAgain()
    {
        var kit = await Kit.StartAsync(OnWithHandy);
        kit.Handy.HoldsModel = true;
        kit.Detector.Find(Game);
        await Until(() => kit.Handy.Closes == 1 && kit.Controller.Status.IsActive);

        await kit.DisposeAsync();

        Assert.Equal(1, kit.Handy.Starts);
        Assert.False(File.Exists(kit.HandyMarker));
    }

    [Fact]
    public async Task AHandyThatAnEarlierRunClosedAndNeverReopened_IsOpenedAtTheStart()
    {
        await using var kit = await Kit.StartAsync(new AppSettings(), before: created =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(created.HandyMarker)!);
            File.WriteAllText(created.HandyMarker, "");
            created.Handy.Running = false;
        });

        Assert.Equal(1, kit.Handy.Starts);
        Assert.False(File.Exists(kit.HandyMarker));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = 0; waited < 5000 && !condition(); waited += 10)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "What was waited for did not happen within five seconds.");
    }

    private sealed class Kit : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "kiri-game-mode-tests-" + Guid.NewGuid().ToString("N"));
        private int _changes;
        private bool _disposed;

        private Kit(AppSettings saved)
        {
            Settings.SaveAsync(saved).GetAwaiter().GetResult();
            var paths = new AppPaths(_root);
            HandyMarker = Path.Combine(paths.CacheDirectory, "game-mode-closed-handy");
            Controller = new GameModeController(
                Settings, Bus, Detector, Pause, Voice, Handy, paths, Clock, NullLogger<GameModeController>.Instance);
            Controller.Changed += (_, _) => Interlocked.Increment(ref _changes);
        }

        public InMemorySettingsService Settings { get; } = new();

        public AppEventBus Bus { get; } = new(NullLogger<AppEventBus>.Instance);

        public FakeDetector Detector { get; } = new();

        public FakePause Pause { get; } = new();

        public FakeVoice Voice { get; } = new();

        public FakeHandy Handy { get; } = new();

        public ManualClock Clock { get; } = new();

        public GameModeController Controller { get; }

        public string HandyMarker { get; }

        // Started, and settled: the settings have been read and the detector is as they say.
        public static async Task<Kit> StartAsync(AppSettings saved, Action<Kit>? before = null)
        {
            var kit = new Kit(saved);
            before?.Invoke(kit);
            await kit.Controller.StartAsync(CancellationToken.None);
            await Until(() => Volatile.Read(ref kit._changes) > 0);
            return kit;
        }

        public async Task SaveAsync(Func<AppSettings, AppSettings> change)
        {
            var settings = change(await Settings.LoadAsync());
            await Settings.SaveAsync(settings);
            await Bus.PublishAsync(new SettingsSaved(settings));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Controller.StopAsync(CancellationToken.None);
            Controller.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeDetector : IGameDetector
    {
        private RunningGame? _running;
        private bool _watching;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public bool WatchGames { get; set; } = true;

        public bool WatchCreativeApps { get; set; }

        // What it reports is only what it would have found while it watches.
        public RunningGame? Current => _watching ? _running : null;

        public event EventHandler? Changed;

        public void Find(RunningGame game)
        {
            _running = game;
            if (_watching)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void End()
        {
            _running = null;
            if (_watching)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Start()
        {
            Starts++;
            _watching = true;
            if (_running is not null)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Stop()
        {
            if (_watching)
            {
                Stops++;
                _watching = false;
            }
        }

        public void Dispose() => Stop();
    }

    private sealed class FakePause : ILocalAiPause
    {
        public LocalAiPauseReason Reason { get; private set; }

        public bool IsPaused => Reason != LocalAiPauseReason.None;

        public int Resumes { get; private set; }

        public event EventHandler? Changed;

        public Task PauseAsync(CancellationToken cancellationToken = default) => PauseAsync(LocalAiPauseReason.User, cancellationToken);

        public Task PauseAsync(LocalAiPauseReason reason, CancellationToken cancellationToken = default)
        {
            if (Reason == LocalAiPauseReason.None)
            {
                Reason = reason;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return Task.CompletedTask;
        }

        public void Resume()
        {
            if (Reason != LocalAiPauseReason.None)
            {
                Reason = LocalAiPauseReason.None;
                Resumes++;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed class FakeVoice : IVoiceSuspension
    {
        public bool IsSuspended { get; private set; }

        public int Suspends { get; private set; }

        public int Resumes { get; private set; }

        public Task SuspendAsync()
        {
            if (!IsSuspended)
            {
                IsSuspended = true;
                Suspends++;
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            if (IsSuspended)
            {
                IsSuspended = false;
                Resumes++;
            }

            return Task.CompletedTask;
        }
    }

    // Handy as game mode sees it: whether it runs, whether it holds a model, and what was done to it. Nothing is started or ended.
    private sealed class FakeHandy : HandyIntegration
    {
        public bool Running { get; set; } = true;

        public bool HoldsModel { get; set; }

        public int Closes { get; private set; }

        public int Starts { get; private set; }

        public override HandyInstallation Detect() => new(@"C:\Handy\handy.exe", "parakeet", true, "Handy's device: Automatic");

        public override bool IsRunning() => Running;

        public override bool IsHoldingModel() => Running && HoldsModel;

        public override bool Close()
        {
            if (!Running)
            {
                return false;
            }

            Running = false;
            Closes++;
            return true;
        }

        public override bool StartHidden()
        {
            Running = true;
            Starts++;
            return true;
        }
    }

    // A clock the test moves by hand: a timer fires when the clock is moved past it.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            while (true)
            {
                ManualTimer? next;
                lock (_gate)
                {
                    var target = _now + by;
                    next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                    if (next is null)
                    {
                        _now = target;
                        return;
                    }

                    by = target - next.Due!.Value;
                    _now = next.Due.Value;
                    next.Due = null;
                }

                next.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate)
                {
                    Due = null;
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
