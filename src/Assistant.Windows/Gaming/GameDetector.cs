using Assistant.Core.Gaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Windows.Gaming;

/// <summary>
/// Tells whether a game is running (<see cref="IGameDetector"/>), spending as little as it can to know:
/// <list type="bullet">
/// <item>Windows says when the window in front changes; nothing looks for that. Each new window is weighed once (<see cref="GameClassifier"/>).</item>
/// <item>A program that is a game by what it is (an emulator, a game engine, a store's library) is known at that first look, with no measuring.
/// So is a creative app, when the user has asked for those to be treated as games: it is known by its name.</item>
/// <item>Only a window that may be a game, and is not known to be, has its use of the graphics card read: every two seconds at first, then less
/// and less often, and only while it stays in front. Two readings in a row must agree, so a moment's work is not taken for a game.</item>
/// <item>Once a game is found nothing is read at all: Windows says when its process ends. The one exception is a game known only by how it
/// behaved, which is the detector's one guess: once a minute it is asked whether it still uses the graphics card, and a program that has
/// stopped for a few minutes is no longer taken for a game, so a wrong guess does not last.</item>
/// </list>
/// It logs that a game was found and why, never which.
/// </summary>
public sealed class GameDetector : IGameDetector
{
    // How many readings of the graphics card in a row must be a game's before a window with nothing else known about it is taken for one.
    internal const int SustainedReadings = 2;

    // How many readings in a row a game known only by its behavior may leave the graphics card alone before it is no longer taken for a game.
    internal const int QuietReadings = 3;

    // How often such a game is asked after.
    internal static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);

    private readonly IGameProbe _probe;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Tracked> _games = [];
    private IDisposable? _foreground;
    private Candidate? _candidate;
    private RunningGame? _current;
    private bool _watchGames = true;
    private bool _watchCreativeApps;
    private bool _running;
    private bool _disposed;

    /// <summary>Creates the detector, not yet watching.</summary>
    public GameDetector(ILogger<GameDetector>? logger = null, TimeProvider? clock = null)
        : this(new WindowsGameProbe(), clock, logger)
    {
    }

    internal GameDetector(IGameProbe probe, TimeProvider? clock = null, ILogger? logger = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public bool WatchGames
    {
        get { lock (_gate) return _watchGames; }
        set { lock (_gate) _watchGames = value; }
    }

    /// <inheritdoc/>
    public bool WatchCreativeApps
    {
        get { lock (_gate) return _watchCreativeApps; }
        set { lock (_gate) _watchCreativeApps = value; }
    }

    /// <inheritdoc/>
    public RunningGame? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _foreground = _probe.WatchForeground(OnForeground);
        }

        GameLog.Started(_logger);

        // A game that was already running: the window in front first, which is the only one that is measured, then every other window once, by
        // what its program is.
        var front = _probe.Foreground();
        Look(front, inFront: true);
        foreach (var window in _probe.VisibleWindows())
        {
            if (window != front)
            {
                Look(window, inFront: false);
            }
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        IDisposable? foreground;
        List<Tracked> games;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            foreground = _foreground;
            _foreground = null;
            DropCandidate();
            games = [.. _games.Values];
            _games.Clear();
            _current = null;
        }

        // Not under the lock: the watcher's thread may be waiting for it in a callback, and stopping waits for that thread.
        foreground?.Dispose();
        foreach (var game in games)
        {
            game.Release();
        }

        GameLog.Stopped(_logger);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    /// <summary>How long until a window that is not yet known to be a game is looked at again: soon at first, then less and less often.</summary>
    internal static TimeSpan NextLook(int looks) => looks switch
    {
        < 10 => TimeSpan.FromSeconds(2),
        < 40 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30),
    };

    // Windows said another window came to the front.
    private void OnForeground(nint window) => Look(window, inFront: true);

    // Weighs a window once. One that may be a game, and is in front, becomes the candidate that is looked at again.
    private void Look(nint window, bool inFront)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            if (inFront)
            {
                DropCandidate();
            }

            if (_probe.Observe(window, out var processId) is not { } facts || _games.ContainsKey(processId))
            {
                return;
            }

            var verdict = GameClassifier.Classify(in facts, _watchGames, _watchCreativeApps);
            if (verdict.IsGame)
            {
                changed = Track(processId, window, facts, verdict.Evidence);
            }
            else if (inFront && verdict.Kind != GameVerdictKind.Never)
            {
                var candidate = new Candidate(window, processId);
                if (verdict.Kind == GameVerdictKind.Watch)
                {
                    candidate.Gpu = _probe.OpenGpu(processId);
                }

                _candidate = candidate;
                candidate.Timer = _clock.CreateTimer(_ => LookAgain(candidate), null, NextLook(0), Timeout.InfiniteTimeSpan);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // The candidate, a little later: is it still in front, has its window changed, and what does the graphics card say.
    private void LookAgain(Candidate candidate)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_running || !ReferenceEquals(_candidate, candidate))
            {
                return;
            }

            if (_probe.Foreground() != candidate.Window
                || _probe.Observe(candidate.Window, out var processId) is not { } facts
                || processId != candidate.ProcessId
                || _games.ContainsKey(processId))
            {
                // No longer in front: Windows tells of the window that is, and that one is weighed afresh.
                DropCandidate();
                return;
            }

            var verdict = GameClassifier.Classify(in facts, _watchGames, _watchCreativeApps);
            if (verdict.Kind == GameVerdictKind.Watch)
            {
                // The counters are opened at one look and read at the next: a share of the card's time is the difference between two readings.
                var usage = candidate.Gpu?.Sample();
                if (usage is { } read)
                {
                    candidate.Sustained = GameClassifier.IsGraphicsLoad(read) ? candidate.Sustained + 1 : 0;
                }
                else
                {
                    // Nothing to read: the process has not used the card yet. The counters are opened again, so that they find it once it does.
                    candidate.Sustained = 0;
                    candidate.Gpu?.Dispose();
                    candidate.Gpu = _probe.OpenGpu(processId);
                }

                verdict = GameClassifier.Classify(
                    facts with { SustainedGraphicsLoad = candidate.Sustained >= SustainedReadings }, _watchGames, _watchCreativeApps);
            }
            else if (!verdict.IsGame)
            {
                candidate.Sustained = 0;
                candidate.Gpu?.Dispose();
                candidate.Gpu = null;
            }

            if (verdict.IsGame)
            {
                DropCandidate();
                changed = Track(processId, candidate.Window, facts, verdict.Evidence);
            }
            else if (verdict.Kind == GameVerdictKind.Never)
            {
                DropCandidate();
            }
            else
            {
                candidate.Looks++;
                candidate.Timer?.Change(NextLook(candidate.Looks), Timeout.InfiniteTimeSpan);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Under the lock. Whether the detector went from no game to a game.
    private bool Track(int processId, nint window, GameFacts facts, GameEvidence evidence)
    {
        // A game is called what its window is called. A creative app's window is called after the user's project, so the app is called by its own name.
        var name = evidence.HasFlag(GameEvidence.CreativeApp)
            ? _probe.ProgramName(facts.ExecutablePath) ?? Path.GetFileNameWithoutExtension(facts.ExecutablePath)
            : facts.DisplayName;
        var tracked = new Tracked(new RunningGame(processId, name, evidence));
        _games[processId] = tracked;
        tracked.Exit = _probe.WatchExit(processId, window, () => Forget(processId, tracked, quiet: false));
        if (!GameClassifier.IsKnown(evidence))
        {
            // Known only by how it behaved: it is asked after now and then, and nothing else is.
            tracked.Gpu = _probe.OpenGpu(processId);
            tracked.Timer = _clock.CreateTimer(_ => Recheck(processId, tracked), null, RecheckEvery, RecheckEvery);
        }

        GameLog.Found(_logger, evidence, _games.Count);
        if (_current is not null)
        {
            return false;
        }

        _current = tracked.Game;
        return true;
    }

    // A game known only by its behavior, a minute later: does it still use the graphics card.
    private void Recheck(int processId, Tracked tracked)
    {
        lock (_gate)
        {
            if (!_running || !_games.TryGetValue(processId, out var known) || !ReferenceEquals(known, tracked))
            {
                return;
            }

            var usage = tracked.Gpu?.Sample();
            if (usage is null)
            {
                // Nothing to read: opened again, as for a candidate, in case the process has started over on the card.
                tracked.Gpu?.Dispose();
                tracked.Gpu = _probe.OpenGpu(processId);
            }

            tracked.Quiet = usage is { } read && GameClassifier.IsStillUsingGraphics(read) ? 0 : tracked.Quiet + 1;
            if (tracked.Quiet < QuietReadings)
            {
                return;
            }
        }

        Forget(processId, tracked, quiet: true);
    }

    // A game is over: Windows said its process ended, or it was a guess and it has left the graphics card alone.
    private void Forget(int processId, Tracked tracked, bool quiet)
    {
        bool changed;
        lock (_gate)
        {
            if (!_games.TryGetValue(processId, out var known) || !ReferenceEquals(known, tracked))
            {
                return;
            }

            _games.Remove(processId);
            changed = ReferenceEquals(_current, tracked.Game);
            if (changed)
            {
                _current = _games.Values.FirstOrDefault()?.Game;
            }

            GameLog.Ended(_logger, quiet, _games.Count);
        }

        tracked.Release();
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Under the lock.
    private void DropCandidate()
    {
        if (_candidate is { } candidate)
        {
            _candidate = null;
            candidate.Timer?.Dispose();
            candidate.Gpu?.Dispose();
        }
    }

    // A game that is running, and what tells of its end.
    private sealed class Tracked(RunningGame game)
    {
        public RunningGame Game { get; } = game;

        public IDisposable? Exit { get; set; }

        // Only for a game known by its behavior alone.
        public IGpuSampler? Gpu { get; set; }

        public ITimer? Timer { get; set; }

        public int Quiet { get; set; }

        public void Release()
        {
            Timer?.Dispose();
            Gpu?.Dispose();
            Exit?.Dispose();
        }
    }

    // The window in front while it is not known whether it is a game's.
    private sealed class Candidate(nint window, int processId)
    {
        public nint Window { get; } = window;

        public int ProcessId { get; } = processId;

        public IGpuSampler? Gpu { get; set; }

        public ITimer? Timer { get; set; }

        public int Looks { get; set; }

        public int Sustained { get; set; }
    }
}

internal static partial class GameLog
{
    [LoggerMessage(EventId = 9140, Level = LogLevel.Information, Message = "The game detector started watching")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 9141, Level = LogLevel.Information, Message = "The game detector stopped watching")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 9142, Level = LogLevel.Information, Message = "A game was found ({Evidence}); {Count} running")]
    public static partial void Found(ILogger logger, GameEvidence evidence, int count);

    [LoggerMessage(EventId = 9143, Level = LogLevel.Information, Message = "A game ended (left the graphics card alone: {Quiet}); {Count} still running")]
    public static partial void Ended(ILogger logger, bool quiet, int count);
}
