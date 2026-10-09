using Assistant.Core.Gaming;
using Assistant.Windows.Gaming;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The game detector's own rules (game mode): what it looks at and when, over a made-up desktop and a clock moved by hand. What is checked is
/// that a game is found, that nothing else is taken for one, and that the detector is idle whenever it can be: it measures only a window that may
/// be a game, and nothing at all once it has found one.
/// </summary>
public sealed class GameDetectorTests
{
    private const nint Desktop = 100, GameWindow = 200, OtherWindow = 300, ThirdWindow = 400;

    private static readonly GameFacts Browser = new()
    {
        ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", WindowClass = "Chrome_WidgetWin_1", Title = "A film", CoversMonitor = true,
    };

    private static readonly GameFacts StoreGame = new()
    {
        ExecutablePath = @"C:\Program Files (x86)\Steam\steamapps\common\BeamNG.drive\BeamNG.drive.exe", WindowClass = "BeamNG", Title = "BeamNG.drive", HasCaption = true,
    };

    private static readonly GameFacts UnknownFullscreen = new()
    {
        ExecutablePath = @"C:\Things\thing.exe", WindowClass = "ThingWindow", Title = "Thing", CoversMonitor = true,
    };

    private static readonly GameFacts UnknownWindowed = UnknownFullscreen with { CoversMonitor = false, HasCaption = true };

    private static readonly GpuUsage Drawing = new(55, 0, 3L << 30);
    private static readonly GpuUsage Idle = new(1, 0, 60L << 20);
    private static readonly GpuUsage PlayingVideo = new(30, 12, 400L << 20);

    [Fact]
    public void AGameInFront_IsFoundAtTheFirstLook_WithoutMeasuringAnything()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Front = GameWindow;

        kit.Detector.Start();

        Assert.Equal(new RunningGame(20, "BeamNG.drive", GameEvidence.GameStore), kit.Detector.Current);
        Assert.Equal(1, kit.Changes);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);
    }

    [Fact]
    public void AGameThatComesToTheFrontLater_IsFoundWhenWindowsSaysSo_AndItsEndIsToldByWindowsToo()
    {
        using var kit = new Kit();
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.Front = Desktop;
        kit.Detector.Start();
        Assert.Null(kit.Detector.Current);

        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.BringToFront(GameWindow);

        Assert.Equal(20, kit.Detector.Current?.ProcessId);
        Assert.Equal(1, kit.Changes);

        // The user switches away: the game is still running, so it is still the game. Nothing is watched or timed for it.
        kit.Probe.BringToFront(Desktop);
        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(20, kit.Detector.Current?.ProcessId);
        Assert.Equal(0, kit.Clock.ActiveTimers);

        kit.Probe.Exit(20);

        Assert.Null(kit.Detector.Current);
        Assert.Equal(2, kit.Changes);
    }

    [Fact]
    public void AGameThatWasAlreadyRunningBehindOtherWindows_IsFoundWhenWatchingBegins()
    {
        using var kit = new Kit();
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Front = Desktop;

        kit.Detector.Start();

        Assert.Equal(20, kit.Detector.Current?.ProcessId);
    }

    [Fact]
    public void AnUnknownFullScreenProgram_IsAGame_AfterTwoReadingsOfABusyCard_AndNotAfterOne()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;

        kit.Detector.Start();
        Assert.Null(kit.Detector.Current);

        kit.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(kit.Detector.Current);

        kit.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(GameEvidence.Fullscreen | GameEvidence.GraphicsLoad, kit.Detector.Current?.Evidence);
        Assert.Equal(1, kit.Changes);

        // Found by its behavior alone, which is a guess: one slow timer keeps asking after it, and nothing else does.
        Assert.Equal(1, kit.Probe.GpuOpen);
        Assert.Equal(1, kit.Clock.ActiveTimers);
        var looks = kit.Probe.Observed;
        kit.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(looks, kit.Probe.Observed);
        Assert.Equal(20, kit.Detector.Current?.ProcessId);
    }

    [Fact]
    public void AGuess_IsDropped_OnceTheProgramHasLeftTheGraphicsCardAloneForAFewMinutes()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();
        kit.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.NotNull(kit.Detector.Current);

        // It was a film in a player nobody had heard of, and it is over: the program stays open and draws nothing.
        kit.Probe.Usage[20] = Idle;
        kit.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(kit.Detector.Current);

        kit.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(kit.Detector.Current);
        Assert.Equal(2, kit.Changes);
        Assert.Equal(0, kit.Probe.GpuOpen);
        Assert.Equal(0, kit.Probe.ExitWatches);
        Assert.Equal(0, kit.Clock.ActiveTimers);
    }

    [Fact]
    public void AGuess_IsKept_WhileTheGameTheUserSwitchedAwayFromStillHoldsWhatItLoaded()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();
        kit.Clock.Advance(TimeSpan.FromSeconds(4));

        kit.Probe.BringToFront(Desktop);
        kit.Probe.Usage[20] = new GpuUsage(0.4, 0, 3L << 30);
        kit.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(20, kit.Detector.Current?.ProcessId);
        Assert.Equal(1, kit.Changes);
    }

    [Fact]
    public void AGameKnownByWhatItIs_IsNeverAskedAfter()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Usage[20] = Idle;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();

        kit.Clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(20, kit.Detector.Current?.ProcessId);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);
    }

    [Fact]
    public void AMomentOfWork_IsNotAGame()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();

        // Busy, idle, busy, idle: never twice in a row.
        foreach (var usage in new[] { Drawing, Idle, Drawing, Idle, Drawing, Idle })
        {
            kit.Probe.Usage[20] = usage;
            kit.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Null(kit.Detector.Current);
    }

    [Fact]
    public void AFullScreenVideo_IsNeverAGame_AndIsLookedAtLessAndLessOften()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Usage[20] = PlayingVideo;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();

        kit.Clock.Advance(TimeSpan.FromSeconds(20));
        var afterTwentySeconds = kit.Probe.Observed;
        kit.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Null(kit.Detector.Current);
        Assert.Equal(0, kit.Changes);

        // Every two seconds at first; over the two hours that follow, every ten seconds and then every thirty: some 260 looks, not 3600.
        Assert.Equal(10, afterTwentySeconds - 1);
        Assert.InRange(kit.Probe.Observed - afterTwentySeconds, 200, 300);
    }

    [Fact]
    public void AProgramThatIsNeverAGame_IsNotLookedAtAgain_AndNothingIsMeasured()
    {
        using var kit = new Kit();
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.Usage[4] = Drawing;
        kit.Probe.Front = Desktop;

        kit.Detector.Start();
        var looks = kit.Probe.Observed;
        kit.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Null(kit.Detector.Current);
        Assert.Equal(looks, kit.Probe.Observed);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);
    }

    [Fact]
    public void AnUnknownWindowedProgram_IsNotMeasured_UntilItFillsTheScreen()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownWindowed);
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();

        kit.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(kit.Detector.Current);
        Assert.Equal(0, kit.Probe.GpuOpened);

        // It goes full screen without the window in front changing: the next look sees it, and two readings later it is a game.
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(20, kit.Detector.Current?.ProcessId);
    }

    [Fact]
    public void WhenAnotherWindowComesToTheFront_TheOneThatWasBeingMeasuredIsLetGoOf()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, UnknownFullscreen);
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();
        kit.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, kit.Probe.GpuOpen);

        kit.Probe.BringToFront(Desktop);

        Assert.Equal(0, kit.Probe.GpuOpen);
        Assert.Equal(0, kit.Clock.ActiveTimers);
        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(kit.Detector.Current);
    }

    [Fact]
    public void WithTwoGamesRunning_ItIsOverOnlyWhenBothHaveEnded()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Add(OtherWindow, 30, StoreGame with { Title = "Another" });
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();
        kit.Probe.BringToFront(OtherWindow);
        Assert.Equal(20, kit.Detector.Current?.ProcessId);
        Assert.Equal(1, kit.Changes);

        kit.Probe.Exit(20);
        Assert.Equal("Another", kit.Detector.Current?.Name);

        kit.Probe.Exit(30);
        Assert.Null(kit.Detector.Current);
    }

    [Fact]
    public void AWindowOfAGameAlreadyFound_ChangesNothing()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Add(ThirdWindow, 20, StoreGame with { Title = "Its settings" });
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();

        kit.Probe.BringToFront(ThirdWindow);

        Assert.Equal("BeamNG.drive", kit.Detector.Current?.Name);
        Assert.Equal(1, kit.Changes);
        Assert.Equal(1, kit.Probe.ExitWatches);
    }

    [Fact]
    public void Stopping_ForgetsTheGames_LetsGoOfEverything_AndSaysNothing()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Add(OtherWindow, 30, UnknownFullscreen);
        kit.Probe.Front = GameWindow;
        kit.Detector.Start();
        kit.Probe.BringToFront(OtherWindow);
        var changes = kit.Changes;

        kit.Detector.Stop();

        Assert.Null(kit.Detector.Current);
        Assert.Equal(changes, kit.Changes);
        Assert.False(kit.Probe.IsWatchingForeground);
        Assert.Equal(0, kit.Probe.ExitWatches);
        Assert.Equal(0, kit.Probe.GpuOpen);
        Assert.Equal(0, kit.Clock.ActiveTimers);

        // And it can be started again.
        kit.Detector.Start();
        Assert.Equal(20, kit.Detector.Current?.ProcessId);
    }

    [Fact]
    public void ACreativeApp_IsLeftAlone_UntilTheUserAsksForThoseToo_AndThenItIsFoundByItsName()
    {
        const string premiere = @"C:\Program Files\Adobe\Adobe Premiere Pro 2025\Adobe Premiere Pro.exe";
        var editing = new GameFacts
        {
            ExecutablePath = premiere, WindowClass = "Premiere Pro", Title = @"Adobe Premiere Pro 2025 - C:\Users\me\Videos\Holiday.prproj", HasCaption = true,
        };
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, editing);
        kit.Probe.ProgramNames[premiere] = "Adobe Premiere Pro 2025";
        kit.Probe.Usage[20] = Drawing;
        kit.Probe.Front = GameWindow;

        // Games only, which is how the detector starts: an editor is never a game, however hard it works the card, and is not even measured.
        Assert.True(kit.Detector.WatchGames);
        Assert.False(kit.Detector.WatchCreativeApps);
        kit.Detector.Start();
        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(kit.Detector.Current);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);

        kit.Detector.Stop();
        kit.Detector.WatchCreativeApps = true;
        kit.Detector.Start();

        // Found at the first look, and called by its own name: its window is called after the user's project.
        Assert.Equal(new RunningGame(20, "Adobe Premiere Pro 2025", GameEvidence.CreativeApp), kit.Detector.Current);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);

        // It is one for as long as it is open, in front or not, and nothing asks after it meanwhile.
        kit.Probe.Add(Desktop, 4, Browser);
        kit.Probe.BringToFront(Desktop);
        kit.Probe.Usage[20] = Idle;
        kit.Clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(20, kit.Detector.Current?.ProcessId);

        kit.Probe.Exit(20);
        Assert.Null(kit.Detector.Current);
    }

    [Fact]
    public void WithOnlyCreativeAppsAskedFor_AGameIsNotLookedFor()
    {
        using var kit = new Kit();
        kit.Probe.Add(GameWindow, 20, StoreGame);
        kit.Probe.Add(OtherWindow, 30, UnknownFullscreen);
        kit.Probe.Usage[30] = Drawing;
        kit.Probe.Front = OtherWindow;
        kit.Detector.WatchGames = false;
        kit.Detector.WatchCreativeApps = true;

        kit.Detector.Start();
        kit.Probe.BringToFront(GameWindow);
        kit.Probe.BringToFront(OtherWindow);
        kit.Clock.Advance(TimeSpan.FromMinutes(10));

        // Not the store's game, and not the full-screen program that keeps the card busy: nothing is measured, and nothing is timed.
        Assert.Null(kit.Detector.Current);
        Assert.Equal(0, kit.Probe.GpuOpened);
        Assert.Equal(0, kit.Clock.ActiveTimers);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 10)]
    [InlineData(39, 10)]
    [InlineData(40, 30)]
    [InlineData(5000, 30)]
    public void TheLongerAWindowStaysUnknown_TheLessOftenItIsLookedAt(int looks, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), GameDetector.NextLook(looks));
    }

    [Fact]
    public void TheRealProbe_ReadsTheWindowsOfThisDesktop_WithoutThrowing()
    {
        // Whatever is on this PC's desktop: every visible window is looked at and weighed the way the detector does at its start.
        var probe = new WindowsGameProbe();
        foreach (var window in probe.VisibleWindows())
        {
            if (probe.Observe(window, out var processId) is { } facts)
            {
                Assert.True(processId > 0);
                _ = GameClassifier.Classify(in facts);
            }
        }

        // The counters of a process that draws nothing say nothing, or say it holds nothing; either way they open and close.
        using var gpu = probe.OpenGpu(Environment.ProcessId);
        var usage = gpu?.Sample();
        Assert.True(usage is null || usage.Value.DedicatedBytes >= 0);
        Assert.False(probe.Observe(0, out _).HasValue);
    }

    private sealed class Kit : IDisposable
    {
        public Kit()
        {
            Detector = new GameDetector(Probe, Clock);
            Detector.Changed += (_, _) => Changes++;
        }

        public FakeProbe Probe { get; } = new();

        public ManualClock Clock { get; } = new();

        public GameDetector Detector { get; }

        public int Changes { get; private set; }

        public void Dispose() => Detector.Dispose();
    }

    private sealed class FakeProbe : IGameProbe
    {
        private readonly Dictionary<nint, (int ProcessId, GameFacts Facts)> _windows = [];
        private readonly Dictionary<int, Action> _exits = [];
        private Action<nint>? _foregroundChanged;

        public nint Front { get; set; }

        public Dictionary<int, GpuUsage> Usage { get; } = [];

        public int Observed { get; private set; }

        public int GpuOpened { get; private set; }

        public int GpuOpen { get; private set; }

        public int ExitWatches => _exits.Count;

        public bool IsWatchingForeground => _foregroundChanged is not null;

        public void Add(nint window, int processId, GameFacts facts) => _windows[window] = (processId, facts);

        public void BringToFront(nint window)
        {
            Front = window;
            _foregroundChanged?.Invoke(window);
        }

        public void Exit(int processId) => _exits[processId]();

        public IDisposable WatchForeground(Action<nint> changed)
        {
            _foregroundChanged = changed;
            return new Undo(() => _foregroundChanged = null);
        }

        public nint Foreground() => Front;

        public IReadOnlyList<nint> VisibleWindows() => [.. _windows.Keys];

        public GameFacts? Observe(nint window, out int processId)
        {
            Observed++;
            if (_windows.TryGetValue(window, out var known))
            {
                processId = known.ProcessId;
                return known.Facts;
            }

            processId = 0;
            return null;
        }

        public Dictionary<string, string> ProgramNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? ProgramName(string executablePath) => ProgramNames.GetValueOrDefault(executablePath);

        public IGpuSampler? OpenGpu(int processId)
        {
            GpuOpened++;
            GpuOpen++;
            return new Sampler(this, processId);
        }

        public IDisposable WatchExit(int processId, nint window, Action exited)
        {
            _exits[processId] = exited;
            return new Undo(() => _exits.Remove(processId));
        }

        private sealed class Sampler(FakeProbe probe, int processId) : IGpuSampler
        {
            private bool _disposed;

            public GpuUsage? Sample() => probe.Usage.TryGetValue(processId, out var usage) ? usage : null;

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    probe.GpuOpen--;
                }
            }
        }
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    // A clock the test moves by hand: a timer fires when the clock is moved past it.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

        public int ActiveTimers => _timers.Count(timer => timer.Due is not null);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (_timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault() is { } next)
            {
                _now = next.Due!.Value;
                next.Fire();
            }

            _now = target;
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void Fire()
            {
                // A timer that repeats is due again a period later; one that does not is set again by whoever it calls, or not at all.
                Due = _period == Timeout.InfiniteTimeSpan ? null : clock._now + _period;
                callback(state);
                if (_disposed)
                {
                    Due = null;
                }
            }

            public void Dispose()
            {
                _disposed = true;
                Due = null;
                clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
