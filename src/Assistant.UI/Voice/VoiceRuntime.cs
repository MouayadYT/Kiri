using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.ViewModels;
using Assistant.Windows.Audio;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Voice;

/// <summary>Where the wake word listener stands, for the Settings page.</summary>
/// <param name="IsOn">Whether the user has turned the wake word on.</param>
/// <param name="IsListening">Whether the microphone is open and the listener is hearing it right now.</param>
/// <param name="Message">What to tell the user when it is on but is not listening ("Starting", "No microphone found"), or <see langword="null"/>.</param>
public sealed record WakeWordStatus(bool IsOn, bool IsListening, string? Message)
{
    /// <summary>The wake word is off.</summary>
    public static WakeWordStatus Off { get; } = new(false, false, null);
}

/// <summary>What the Voice settings page reads of the voice as it runs (PROJECT_SPEC §4.9, step 125).</summary>
public interface IVoiceRuntime
{
    /// <summary>Where the wake word listener stands.</summary>
    WakeWordStatus WakeWord { get; }

    /// <summary>Raised on any thread when <see cref="WakeWord"/> changes.</summary>
    event EventHandler? WakeWordChanged;
}

/// <summary>
/// Lets go of the voice for a while and takes it up again (game mode): while it is suspended the voice, the recognizer and the wake word's listener hold
/// no model, the microphone is closed, and nothing loads them, whatever the settings say.
/// </summary>
public interface IVoiceSuspension
{
    /// <summary>Whether the voice is suspended.</summary>
    bool IsSuspended { get; }

    /// <summary>Stops what is being said or listened to, unloads every voice model and closes the microphone. Suspending again changes nothing.</summary>
    Task SuspendAsync();

    /// <summary>Puts the voice back as the settings say, as at the start: the wake word listens again if it is on. Resuming what is not suspended changes nothing.</summary>
    Task ResumeAsync();
}

/// <summary>
/// Keeps the voice running the way the settings say (PROJECT_SPEC §4.2, step 125): the chosen text-to-speech engine is selected at the start and again whenever
/// the user chooses another (any speech stops, the old engine is let go of, the new one is loaded and made ready), the wake word listens only while the user
/// has turned it on, and the memory the voice takes is given back when it is not used. It does nothing the user can see.
/// </summary>
internal sealed class VoiceRuntime : IHostedService, IVoiceRuntime, IVoiceSuspension, IDisposable
{
    /// <summary>What the wake word's status and the microphone button say while game mode has the voice suspended.</summary>
    internal const string SuspendedMessage = "Paused by game mode while your game or creative app is running.";

    // How long the speech recognizer stays in memory after the last request, when the wake word is off.
    private static readonly TimeSpan RecognizerIdle = TimeSpan.FromMinutes(5);

    // How soon the microphone is tried again after it failed while only the wake word was using it (a headset unplugged and plugged back).
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly ITextToSpeechService _speech;
    private readonly ISpeechToTextService _recognizer;
    private readonly IWakeWordService _wakeWord;
    private readonly MicrophoneRouter _router;
    private readonly MicrophoneChoice _microphone;
    private readonly TimeProvider _clock;
    private readonly ILogger<VoiceRuntime> _logger;
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private ITimer? _timer;
    private bool _wakeOn;
    private bool _wakeWanted;
    private string? _wakeMessage;
    private DateTimeOffset _retryAt;
    private bool _stopped;
    private bool _suspended;

    public VoiceRuntime(
        ISettingsService settings, IAppEventBus events, ITextToSpeechService speech, ISpeechToTextService recognizer, IWakeWordService wakeWord,
        MicrophoneRouter router, TimeProvider clock, ILogger<VoiceRuntime> logger, MicrophoneChoice? microphone = null)
    {
        _microphone = microphone ?? new MicrophoneChoice();
        _settings = settings;
        _events = events;
        _speech = speech;
        _recognizer = recognizer;
        _wakeWord = wakeWord;
        _router = router;
        _clock = clock;
        _logger = logger;
        _router.WakeWordUnavailable += OnWakeWordUnavailable;
        _wakeWord.StatusChanged += OnWakeWordModelChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? WakeWordChanged;

    /// <inheritdoc/>
    public WakeWordStatus WakeWord
    {
        get
        {
            bool on;
            bool suspended;
            string? message;
            lock (_gate)
            {
                on = _wakeOn;
                suspended = _suspended && _wakeWanted;
                message = _wakeMessage;
            }

            if (suspended)
            {
                // The user has it on; it is game mode that keeps it from listening.
                return new WakeWordStatus(true, false, SuspendedMessage);
            }

            if (!on)
            {
                return WakeWordStatus.Off;
            }

            var model = _wakeWord.Status;
            message ??= model.State switch
            {
                VoiceEngineState.NotInstalled or VoiceEngineState.Failed => model.Message,
                VoiceEngineState.Loading or VoiceEngineState.NotLoaded => "Starting…",
                _ => null,
            };
            return new WakeWordStatus(true, message is null && _router.IsListeningForWakeWord, message);
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.Subscribe<VoiceRuntime, SettingsSaved>(this, static (runtime, saved, _) => runtime.ApplyAsync(saved.Settings.Voice, atStart: false));
        _timer = _clock.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        try
        {
            await ApplyAsync((await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Voice, atStart: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Settings that cannot be read leave the voice as chosen by default: off, and loaded when it is first needed.
            VoiceRuntimeLog.NotStarted(_logger, exception.GetType().Name);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopped = true;
        }

        _router.SetWakeWord(false);
        _speech.StopAll();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _subscription?.Dispose();
        _timer?.Dispose();
        _router.WakeWordUnavailable -= OnWakeWordUnavailable;
        _wakeWord.StatusChanged -= OnWakeWordModelChanged;
    }

    /// <inheritdoc/>
    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _suspended;
            }
        }
    }

    /// <inheritdoc/>
    public async Task SuspendAsync()
    {
        bool wakeWasOn;
        lock (_gate)
        {
            if (_suspended || _stopped)
            {
                return;
            }

            _suspended = true;
            wakeWasOn = _wakeOn;
        }

        VoiceRuntimeLog.Suspended(_logger, true);

        // The microphone first: a request that is being listened to ends, the wake word stops, and nothing starts either until the voice is resumed.
        _router.SetVoiceInputEnabled(false, SuspendedMessage);
        if (wakeWasOn)
        {
            TurnWakeWord(false);
        }
        else
        {
            // Setup may have loaded the listener to try it, with the wake word still off.
            _wakeWord.Unload();
        }

        // Then the models: the recognizer, and the voice with whatever it is saying.
        _speech.KeepLoaded = false;
        _recognizer.Unload();
        try
        {
            await _speech.UnloadAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An engine that will not unload is one that was failing to load; there is nothing more to let go of.
            VoiceRuntimeLog.NotStarted(_logger, exception.GetType().Name);
        }

        WakeWordChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task ResumeAsync()
    {
        lock (_gate)
        {
            if (!_suspended)
            {
                return;
            }

            _suspended = false;
        }

        VoiceRuntimeLog.Suspended(_logger, false);
        try
        {
            // As at the start: the wake word listens again if it is on, and the rest loads when it is needed.
            await ApplyAsync((await _settings.LoadAsync().ConfigureAwait(false)).Voice, atStart: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            VoiceRuntimeLog.NotStarted(_logger, exception.GetType().Name);
        }

        WakeWordChanged?.Invoke(this, EventArgs.Empty);
    }

    // The settings as saved, or as read at the start: the engine the user chose, and whether the wake word is on.
    private async Task ApplyAsync(VoiceSettings voice, bool atStart)
    {
        await _recognizer.ConfigureAsync(voice).ConfigureAwait(false);
        bool suspended;
        lock (_gate)
        {
            suspended = _suspended;
            _wakeWanted = voice.VoiceInputEnabled && voice.WakeWordEnabled;
        }

        // While game mode has the voice suspended the settings are kept for later: nothing listens and nothing loads.
        _router.SetVoiceInputEnabled(voice.VoiceInputEnabled && !suspended, suspended ? SuspendedMessage : null);
        var wakeEnabled = !suspended && voice.VoiceInputEnabled && voice.WakeWordEnabled;
        bool wakeChanged;
        bool microphoneChanged;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            wakeChanged = atStart ? wakeEnabled : _wakeOn != wakeEnabled;

            // The microphone the user chose is used the next time one is opened; the wake word, which keeps one open, is started again on it.
            microphoneChanged = !atStart && !string.Equals(_microphone.DeviceId, string.IsNullOrWhiteSpace(voice.MicrophoneDeviceId) ? null : voice.MicrophoneDeviceId, StringComparison.Ordinal);
            _microphone.DeviceId = voice.MicrophoneDeviceId;
        }

        // The engine stays loaded while the wake word is on, because the next answer may come at any moment: its first sound then does not wait for a load.
        _speech.KeepLoaded = wakeEnabled;

        // Choosing another engine in Settings loads it at once and says when it is ready; the engine read at the start loads when it is needed, unless the
        // wake word is on.
        if (atStart || !string.Equals(_speech.Status.EngineId, voice.TextToSpeechModelId, StringComparison.Ordinal))
        {
            await _speech.SelectEngineAsync(voice.TextToSpeechModelId, load: !suspended && (!atStart || wakeEnabled)).ConfigureAwait(false);
        }

        if (wakeChanged)
        {
            TurnWakeWord(wakeEnabled);
        }
        else if (microphoneChanged && wakeEnabled)
        {
            TurnWakeWord(false);
            TurnWakeWord(true);
        }
    }

    private void TurnWakeWord(bool on)
    {
        VoiceRuntimeLog.WakeWordTurned(_logger, on);
        lock (_gate)
        {
            _wakeOn = on;
            _wakeMessage = null;
            _retryAt = default;
        }

        if (on)
        {
            // The listener's model and the recognizer are loaded now, in the background, so that "Kiri" is heard and the request after it is understood.
            _ = _wakeWord.WarmUpAsync();
            _ = _recognizer.WarmUpAsync();
        }

        _router.SetWakeWord(on);
        if (!on)
        {
            _wakeWord.Unload();
        }

        WakeWordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWakeWordUnavailable(object? sender, MicrophoneFailure failure)
    {
        VoiceRuntimeLog.WakeWordMicrophoneFailed(_logger, failure);
        lock (_gate)
        {
            _wakeMessage = VoiceInputViewModel.Describe(failure);
            _retryAt = _clock.GetUtcNow() + RetryAfter;
        }

        WakeWordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWakeWordModelChanged(object? sender, EventArgs e) => WakeWordChanged?.Invoke(this, EventArgs.Empty);

    // Every half minute: a microphone that failed is tried again, and a recognizer that nobody used for a while is let go of.
    private void Tick()
    {
        var now = _clock.GetUtcNow();
        bool retry;
        bool idle;
        lock (_gate)
        {
            retry = _wakeOn && _wakeMessage is not null && _retryAt != default && now >= _retryAt;
            if (retry)
            {
                _wakeMessage = null;
                _retryAt = default;
            }

            idle = !_wakeOn && !_router.IsListeningToRequest && _router.LastRequestEndedAt is { } ended && now - ended >= RecognizerIdle;
        }

        if (retry)
        {
            _router.SetWakeWord(true);
            WakeWordChanged?.Invoke(this, EventArgs.Empty);
        }

        if (idle)
        {
            _recognizer.Unload();
        }
    }
}

internal static partial class VoiceRuntimeLog
{
    [LoggerMessage(EventId = 9120, Level = LogLevel.Warning, Message = "The voice settings could not be read at start: {ExceptionType}")]
    public static partial void NotStarted(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 9121, Level = LogLevel.Information, Message = "The wake word was turned {On}")]
    public static partial void WakeWordTurned(ILogger logger, bool on);

    [LoggerMessage(EventId = 9122, Level = LogLevel.Warning, Message = "The microphone failed while only the wake word was listening: {Reason}")]
    public static partial void WakeWordMicrophoneFailed(ILogger logger, MicrophoneFailure reason);

    [LoggerMessage(EventId = 9123, Level = LogLevel.Information, Message = "The voice was suspended: {Suspended}")]
    public static partial void Suspended(ILogger logger, bool suspended);
}
