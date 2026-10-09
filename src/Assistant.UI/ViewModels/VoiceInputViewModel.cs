using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.Core.Voice;
using Assistant.UI.Orb;
using Assistant.UI.Voice;
using Assistant.Windows.Audio;

namespace Assistant.UI.ViewModels;

/// <summary>What a spoken request came to when voice input ended by itself or because the speaker was done.</summary>
/// <param name="Text">The request, written as one (<see cref="SpokenRequestText.Normalize"/>), or empty when nothing was understood.</param>
/// <param name="Reason">Why voice input ended.</param>
/// <param name="FromWakeWord">Whether it was the wake word that started it.</param>
public sealed record VoiceUtterance(string Text, SpeechEndReason Reason, bool FromWakeWord)
{
    /// <summary>Whether there is a request to ask.</summary>
    public bool HasRequest => Text.Length > 0;
}

/// <summary>
/// Voice input for one surface (PROJECT_SPEC §4.2, step 125). The microphone is open only between <see cref="Start"/> and the end of the request, which the
/// user controls; surfaces also stop it when they are dismissed or lose focus, so it is never left listening (PROJECT_SPEC §3.1 P2). While it listens, it reports
/// the microphone's level to the surface's visualizer and the orb, and, with speech recognition (<see cref="IVoiceInput"/>), the words as they are
/// recognized: <see cref="Transcript"/> grows while the user speaks, and when they stop (a pause, or pressing the microphone again)
/// <see cref="UtteranceEnded"/> says what was asked, to be asked as a spoken request. Starting it silences any answer that is being spoken.
/// </summary>
public sealed class VoiceInputViewModel : INotifyPropertyChanged, IVoiceLevelSource
{
    private readonly IMicrophoneLevelMeter _meter;
    private readonly IVoiceInput? _input;
    private readonly ISpokenAnswers? _speech;
    private IMicrophoneLevelSession? _session;
    private IVoiceListening? _listening;
    private bool _fromWakeWord;
    private string? _failureMessage;
    private string _transcript = "";

    public VoiceInputViewModel(IMicrophoneLevelMeter meter, IVoiceInput? input = null, ISpokenAnswers? speech = null)
    {
        _meter = meter;
        _input = input;
        _speech = speech;
        ToggleCommand = new RelayCommand(Toggle);
        OrbAmplitude = new VoiceLevelAmplitude(this);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised on the UI thread when voice input has ended for good: the speaker paused, said they were done, or nothing was heard. Not raised when
    /// voice input is turned off by <see cref="Stop"/>, which throws what was heard away.
    /// </summary>
    public event EventHandler<VoiceUtterance>? UtteranceEnded;

    /// <summary>Whether the microphone is on.</summary>
    public bool IsListening => _session is not null || _listening is not null;

    /// <summary>Whether voice input recognizes speech, as opposed to only measuring how loud the microphone is.</summary>
    public bool RecognizesSpeech => _input is not null;

    /// <summary>
    /// Whether the assistant orb shows: while the microphone listens for speech. It follows the same microphone level as the glow (<see cref="OrbAmplitude"/>).
    /// </summary>
    public bool ShowsOrb => IsListening && RecognizesSpeech;

    /// <summary>The microphone's loudness, normalized for the orb: the same reading the voice glow follows, on the scale the orb expects.</summary>
    public IOrbAmplitudeSource OrbAmplitude { get; }

    /// <summary>
    /// The words heard so far, written as a request, while voice input listens: empty until something is understood. It is private text, shown in the
    /// surface and never logged.
    /// </summary>
    public string Transcript
    {
        get => _transcript;
        private set
        {
            if (_transcript != value)
            {
                _transcript = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTranscript));
            }
        }
    }

    /// <summary>Whether <see cref="Transcript"/> has words.</summary>
    public bool HasTranscript => _transcript.Length > 0;

    /// <summary>Whether the wake word started the voice input that is on.</summary>
    public bool StartedByWakeWord => _fromWakeWord;

    /// <summary>
    /// Why the microphone stopped by itself, in words for the user, or <see langword="null"/>. It clears when voice input starts or stops again.
    /// </summary>
    public string? FailureMessage
    {
        get => _failureMessage;
        private set
        {
            if (_failureMessage != value)
            {
                _failureMessage = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Turns voice input on, and when it is on, says the user is done speaking (the request is recognized as far as it was heard and asked).
    /// </summary>
    public ICommand ToggleCommand { get; }

    /// <summary>Turns the microphone on. Call it on the UI thread.</summary>
    public void Start() => Start(null);

    /// <summary>
    /// Turns the microphone on for the request that follows the wake word (<paramref name="handoff"/>): what was heard around the word comes first. Call it
    /// on the UI thread.
    /// </summary>
    public void Start(WakeHandoff? handoff)
    {
        if (_input?.IsEnabled == false)
        {
            FailureMessage = _input.UnavailableMessage ?? "Enable voice control in Settings → Voice.";
            return;
        }
        if (IsListening)
        {
            return;
        }

        FailureMessage = null;
        Transcript = "";
        _fromWakeWord = handoff is not null;

        // A new request is more important than what the Assistant is saying: it goes quiet at once.
        _speech?.Stop();
        var context = SynchronizationContext.Current;
        void Post(Action action)
        {
            if (context is null)
            {
                action();
            }
            else
            {
                context.Post(_ => action(), null);
            }
        }

        if (_input is not null)
        {
            var holder = new ListeningHolder();
            var listening = _input.Listen(
                new VoiceListenRequest { Handoff = handoff },
                failure => Post(() => OnFailed(holder.Listening, failure)));
            holder.Listening = listening;
            _listening = listening;
            listening.Transcribed += (_, e) => Post(() => OnTranscribed(listening, e));
        }
        else
        {
            var holder = new SessionHolder();
            holder.Session = _meter.Start(failure => Post(() => OnFailed(holder.Session, failure)));
            _session = holder.Session;
        }

        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ShowsOrb));
    }

    /// <summary>Turns the microphone off, if it is on, throws away what was heard and clears any failure message.</summary>
    public void Stop()
    {
        FailureMessage = null;
        Close();
        Transcript = "";
        _fromWakeWord = false;
    }

    /// <summary>
    /// The user is done speaking: what was heard is recognized as far as it goes and asked. Without speech recognition, or when the microphone is off, it
    /// is <see cref="Stop"/>.
    /// </summary>
    public void Finish()
    {
        if (_listening is { } listening)
        {
            listening.Finish();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>Turns the microphone on if it is off, and, if it is on, says the request is over.</summary>
    public void Toggle()
    {
        if (IsListening)
        {
            Finish();
        }
        else
        {
            Start();
        }
    }

    /// <inheritdoc/>
    public double ReadLevel() => _listening?.Level ?? _session?.Level ?? 0;

    /// <summary>The words shown to the user for a microphone failure.</summary>
    public static string Describe(MicrophoneFailure failure) => failure switch
    {
        MicrophoneFailure.NoMicrophone => "No microphone found",
        MicrophoneFailure.AccessDenied => "Microphone access is off in Settings",
        MicrophoneFailure.Disconnected => "Microphone disconnected",
        _ => "Microphone unavailable",
    };

    private void OnFailed(object? session, MicrophoneFailure failure)
    {
        if (session is not null && (ReferenceEquals(session, _session) || ReferenceEquals(session, _listening)))
        {
            Close();
            Transcript = "";
            FailureMessage = Describe(failure);
        }
    }

    // Words from the recognizer, on the UI thread: shown as they grow, and when the request ends it is asked.
    private void OnTranscribed(IVoiceListening listening, SpeechTranscriptEventArgs e)
    {
        if (!ReferenceEquals(listening, _listening))
        {
            return;
        }

        var request = SpokenRequestText.Normalize(e.Text, endMark: e.IsFinal);
        if (e.Ended is not { } reason)
        {
            Transcript = request;
            return;
        }

        var fromWakeWord = _fromWakeWord;
        Close();
        Transcript = "";
        _fromWakeWord = false;

        // A recognizer that cannot be used ends the request with nothing heard: the user is told why instead of nothing happening.
        if (request.Length == 0 && reason == SpeechEndReason.Finished && _input?.RecognizerStatus is { State: VoiceEngineState.NotInstalled or VoiceEngineState.Failed } status)
        {
            FailureMessage = status.State == VoiceEngineState.NotInstalled ? "Speech recognition isn't installed" : "Speech recognition isn't working";
        }

        UtteranceEnded?.Invoke(this, new VoiceUtterance(request, reason, fromWakeWord));
    }

    private void Close()
    {
        var closed = false;
        if (_session is { } session)
        {
            _session = null;
            session.Dispose();
            closed = true;
        }

        if (_listening is { } listening)
        {
            _listening = null;
            listening.Dispose();
            closed = true;
        }

        if (closed)
        {
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(ShowsOrb));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // Let the failure callback see the session it belongs to, which exists only once the call that makes it returns.
    private sealed class SessionHolder
    {
        public IMicrophoneLevelSession? Session { get; set; }
    }

    private sealed class ListeningHolder
    {
        public IVoiceListening? Listening { get; set; }
    }
}
