using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Gaming;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Gaming;

/// <summary>Where game mode stands, for the Settings page and the tray.</summary>
/// <param name="Enabled">Whether the user has game mode on, for games, for creative apps or for both.</param>
/// <param name="IsActive">Whether a game (or a creative app that is watched for) is running and the AI models are released for it.</param>
/// <param name="Game">What the running game or creative app is called, or <see langword="null"/> when none runs. It is shown to the user and never logged.</param>
/// <param name="Overridden">Whether one is running but the user resumed the local AI, which holds until it ends.</param>
public sealed record GameModeStatus(bool Enabled, bool IsActive, string? Game, bool Overridden)
{
    /// <summary>Game mode is off.</summary>
    public static GameModeStatus Off { get; } = new(false, false, null, false);
}

/// <summary>What the rest of the app reads of game mode.</summary>
public interface IGameMode
{
    /// <summary>Where game mode stands.</summary>
    GameModeStatus Status { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Game mode: while a game runs, every AI model the Assistant holds is let go of, so the game has the graphics memory and the processor to itself, and
/// when the game ends they are put back as they were. A creative app that is open (a video editor, a 3D tool, a photo editor) is treated the same
/// way when the user has asked for that; the two are separate choices.
/// <list type="bullet">
/// <item>The local model is paused the way the tray's Pause Local AI pauses it: its engine exits, which is what gives its memory back, and nothing
/// loads it until it is resumed. A model the user had paused themselves stays paused afterwards.</item>
/// <item>The voice, the recognizer and the wake word's listener are unloaded and the microphone is closed (<see cref="IVoiceSuspension"/>).</item>
/// <item>A recognizer shared with Handy is released by closing Handy, when it holds a model, and starting it again afterwards: Handy has no command
/// that unloads its model.</item>
/// </list>
/// Resuming is as cheap as the start of the app: the wake word listens again if it is on, and the models load when they are next needed. The user's
/// Resume Local AI in the tray menu wins over a running game, until that game ends. While game mode is off the detector does not run at all.
/// </summary>
internal sealed class GameModeController : IHostedService, IGameMode, IDisposable
{
    // A game that ends is often about to start again: a launcher hands over to the game, a game restarts itself to change its settings. The models
    // come back only when no game has run for this long.
    internal static readonly TimeSpan ResumeAfter = TimeSpan.FromSeconds(8);

    // How long the "stop" the Assistant sends Handy for a request that was being listened to is given to arrive before Handy is closed.
    private static readonly TimeSpan HandySettle = TimeSpan.FromSeconds(1.5);

    private const string HandyRecognizerId = "handy";

    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly IGameDetector _detector;
    private readonly ILocalAiPause _pause;
    private readonly IVoiceSuspension _voice;
    private readonly HandyIntegration _handy;
    private readonly MicrophoneRouter? _router;
    private readonly TimeProvider _clock;
    private readonly ILogger<GameModeController> _logger;
    private readonly string _handyMarker;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private ITimer? _resumeTimer;
    private GameModeSettings _gameMode = new();
    private VoiceSettings _voiceSettings = new();
    private GameModeStatus _status = GameModeStatus.Off;
    private bool _enabled;
    private bool _watching;
    private bool _watchingGames;
    private bool _watchingCreativeApps;
    private bool _engaged;
    private bool _overridden;
    private bool _pausedLocalAi;
    private bool _closedHandy;
    private bool _stopped;

    public GameModeController(
        ISettingsService settings, IAppEventBus events, IGameDetector detector, ILocalAiPause pause, IVoiceSuspension voice, HandyIntegration handy,
        AppPaths paths, TimeProvider clock, ILogger<GameModeController> logger, MicrophoneRouter? router = null)
    {
        _settings = settings;
        _events = events;
        _detector = detector;
        _pause = pause;
        _voice = voice;
        _handy = handy;
        _router = router;
        _clock = clock;
        _logger = logger;
        _handyMarker = Path.Combine(paths.CacheDirectory, "game-mode-closed-handy");
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public GameModeStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.Subscribe<GameModeController, SettingsSaved>(this, static (controller, saved, _) => controller.ApplyAsync(saved.Settings));
        _detector.Changed += OnGameChanged;
        _pause.Changed += OnPauseChanged;

        // Off the starting thread: reading the settings and the first look at the windows that are open take a moment the app's start does not wait for.
        _ = Task.Run(StartWatchingAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopped = true;
        }

        _detector.Changed -= OnGameChanged;
        _pause.Changed -= OnPauseChanged;
        lock (_gate)
        {
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }

        _detector.Stop();

        // The Assistant is closing while a game runs: Handy is given back as it was found. The models are not: nothing will use them.
        ReopenHandy();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _subscription?.Dispose();
        lock (_gate)
        {
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }
    }

    private async Task StartWatchingAsync()
    {
        try
        {
            // A Handy that an earlier run closed for a game and never reopened (the Assistant was ended first) is reopened now.
            if (File.Exists(_handyMarker))
            {
                if (!_handy.IsRunning())
                {
                    _handy.StartHidden();
                }

                File.Delete(_handyMarker);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameModeLog.StepFailed(_logger, "reopen-handy", exception.GetType().Name);
        }

        try
        {
            await ApplyAsync(await _settings.LoadAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Settings that cannot be read leave game mode as it is by default: off.
            GameModeLog.StepFailed(_logger, "settings", exception.GetType().Name);
        }
    }

    // The settings as saved, or as read at the start: whether to watch for games at all. Whoever saved them is not kept waiting for what follows.
    private Task ApplyAsync(AppSettings settings)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            _gameMode = settings.GameMode;
            _voiceSettings = settings.Voice;
            _enabled = settings.GameMode.Games || settings.GameMode.CreativeApps;
        }

        _ = Task.Run(ReconcileAsync);
        return Task.CompletedTask;
    }

    // The detector found a game, or the last one ended. It may be called on the detector's own thread, which is not kept.
    private void OnGameChanged(object? sender, EventArgs e)
    {
        var running = _detector.Current is not null;
        lock (_gate)
        {
            _resumeTimer?.Dispose();

            // Not at once when the last game ended: see ResumeAfter.
            _resumeTimer = running || _stopped ? null : _clock.CreateTimer(_ => _ = ReconcileAsync(), null, ResumeAfter, Timeout.InfiniteTimeSpan);
        }

        if (running)
        {
            _ = Task.Run(ReconcileAsync);
        }
    }

    // The local AI was paused or resumed. Resumed while game mode has the models released, it is the user's doing (game mode resumes only after it
    // has stood down), and it stays resumed until that game ends.
    private void OnPauseChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_engaged || _pause.IsPaused)
            {
                return;
            }

            _overridden = true;
        }

        GameModeLog.Overridden(_logger);
        _ = Task.Run(ReconcileAsync);
    }

    // Makes what is so what should be so: the models released while a game runs, game mode is on and the user has not resumed them; back otherwise.
    private async Task ReconcileAsync()
    {
        // One at a time, in the order they were asked for: letting go and putting back never overlap.
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            // The detector runs only while game mode is on, looking for what the user chose: games, creative apps, or both. Its first look reads
            // every open window once, which is why this is never done on the thread that saved the settings or on the UI thread.
            bool watch;
            bool wasWatching;
            bool games;
            bool creativeApps;
            bool scopeChanged;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                watch = _enabled;
                wasWatching = _watching;
                games = _gameMode.Games;
                creativeApps = _gameMode.CreativeApps;
                scopeChanged = games != _watchingGames || creativeApps != _watchingCreativeApps;
                _watching = watch;
                _watchingGames = games;
                _watchingCreativeApps = creativeApps;
            }

            // A change of what is looked for starts the detector over: what it found was found under the old choice.
            if (wasWatching && (!watch || scopeChanged))
            {
                _detector.Stop();
            }

            if (watch && (!wasWatching || scopeChanged))
            {
                GameModeLog.Turned(_logger, games, creativeApps);
                _detector.WatchGames = games;
                _detector.WatchCreativeApps = creativeApps;
                _detector.Start();
            }
            else if (!watch && wasWatching)
            {
                GameModeLog.Turned(_logger, false, false);
            }

            RunningGame? game;
            bool engage;
            bool release;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                game = _enabled ? _detector.Current : null;
                if (game is null)
                {
                    _overridden = false;
                }

                var want = game is not null && !_overridden;
                engage = want && !_engaged;
                release = !want && _engaged;
            }

            if (engage)
            {
                await EngageAsync().ConfigureAwait(false);
            }
            else if (release)
            {
                await ReleaseAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _status = new GameModeStatus(_enabled, _engaged, game?.Name, _overridden);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            GameModeLog.StepFailed(_logger, "reconcile", exception.GetType().Name);
        }
        finally
        {
            _turn.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // A game is running: every model is let go of.
    private async Task EngageAsync()
    {
        GameModeSettings gameMode;
        VoiceSettings voiceSettings;
        lock (_gate)
        {
            _engaged = true;
            gameMode = _gameMode;
            voiceSettings = _voiceSettings;
        }

        GameModeLog.Engaged(_logger);
        var wasListening = _router?.IsListeningToRequest == true;

        // The local model: only if it is not paused already. One the user paused is theirs to resume.
        if (!_pause.IsPaused)
        {
            await StepAsync("local-ai", () => _pause.PauseAsync(LocalAiPauseReason.GameMode)).ConfigureAwait(false);
            lock (_gate)
            {
                // Still game mode's pause: the user may have resumed while the model was being unloaded, which OnPauseChanged has taken note of.
                _pausedLocalAi = _pause.Reason == LocalAiPauseReason.GameMode;
            }
        }

        await StepAsync("voice", _voice.SuspendAsync).ConfigureAwait(false);

        if (gameMode.ReleaseSharedRecognizer && voiceSettings.VoiceInputEnabled
            && string.Equals(voiceSettings.SpeechRecognitionModelId, HandyRecognizerId, StringComparison.Ordinal))
        {
            await StepAsync("handy", async () =>
            {
                if (wasListening)
                {
                    await Task.Delay(HandySettle, _clock).ConfigureAwait(false);
                }

                CloseHandy();
            }).ConfigureAwait(false);
        }
    }

    // No game runs any more, or the user wants the local AI now: everything is put back as it was.
    private async Task ReleaseAsync()
    {
        bool resume;
        lock (_gate)
        {
            _engaged = false;
            resume = _pausedLocalAi;
            _pausedLocalAi = false;
        }

        GameModeLog.Released(_logger);

        // Only the pause game mode made itself, and only if it is still that one.
        if (resume && _pause.Reason == LocalAiPauseReason.GameMode)
        {
            await StepAsync("local-ai", () =>
            {
                _pause.Resume();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        await StepAsync("voice", _voice.ResumeAsync).ConfigureAwait(false);
        ReopenHandy();
    }

    // Handy is closed only while it holds a model: one that has already let go of it by itself is left alone.
    private void CloseHandy()
    {
        if (!_handy.IsHoldingModel())
        {
            return;
        }

        // Written before Handy is closed, so that a run that ends before the game does still knows to reopen it.
        TryWriteMarker();
        if (_handy.Close())
        {
            lock (_gate)
            {
                _closedHandy = true;
            }

            GameModeLog.HandyClosed(_logger, true);
        }
        else
        {
            TryDeleteMarker();
        }
    }

    private void ReopenHandy()
    {
        lock (_gate)
        {
            if (!_closedHandy)
            {
                return;
            }

            _closedHandy = false;
        }

        try
        {
            if (!_handy.IsRunning())
            {
                _handy.StartHidden();
            }

            GameModeLog.HandyClosed(_logger, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            GameModeLog.StepFailed(_logger, "reopen-handy", exception.GetType().Name);
        }

        TryDeleteMarker();
    }

    // One part of letting go or putting back: a part that fails does not keep the others from being done.
    private async Task StepAsync(string step, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            GameModeLog.StepFailed(_logger, step, exception.GetType().Name);
        }
    }

    private void TryWriteMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_handyMarker)!);
            File.WriteAllText(_handyMarker, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameModeLog.StepFailed(_logger, "marker", exception.GetType().Name);
        }
    }

    private void TryDeleteMarker()
    {
        try
        {
            File.Delete(_handyMarker);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameModeLog.StepFailed(_logger, "marker", exception.GetType().Name);
        }
    }
}

internal static partial class GameModeLog
{
    [LoggerMessage(EventId = 9150, Level = LogLevel.Information, Message = "Game mode now watches for games: {Games}, creative apps: {CreativeApps}")]
    public static partial void Turned(ILogger logger, bool games, bool creativeApps);

    [LoggerMessage(EventId = 9151, Level = LogLevel.Information, Message = "Game mode released the AI models: a game or a creative app is running")]
    public static partial void Engaged(ILogger logger);

    [LoggerMessage(EventId = 9152, Level = LogLevel.Information, Message = "Game mode put the AI models back")]
    public static partial void Released(ILogger logger);

    [LoggerMessage(EventId = 9153, Level = LogLevel.Information, Message = "The user resumed the local AI while a game runs")]
    public static partial void Overridden(ILogger logger);

    [LoggerMessage(EventId = 9154, Level = LogLevel.Information, Message = "Handy was closed for a game: {Closed}")]
    public static partial void HandyClosed(ILogger logger, bool closed);

    [LoggerMessage(EventId = 9155, Level = LogLevel.Warning, Message = "A step of game mode failed ({Step}): {ExceptionType}")]
    public static partial void StepFailed(ILogger logger, string step, string exceptionType);
}
