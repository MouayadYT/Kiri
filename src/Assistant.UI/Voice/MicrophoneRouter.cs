using Assistant.Core.Voice;
using Assistant.Windows.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Voice;

/// <summary>
/// The one place that holds the microphone for voice (PROJECT_SPEC §4.2, step 125). The microphone is open only while somebody needs it: a request is being
/// listened to, or the wake word is on. Its audio goes to one of two listeners and never to both: while a request is being recognized it is the recognizer's,
/// and otherwise it is the wake-word listener's (a request that says "Kiri" in the middle of itself does not wake anything). When the wake word is heard the
/// router keeps the audio around it and hands it, with the audio that follows, to the request that is about to start, so that "Kiri, what's on my calendar
/// tomorrow?" loses nothing. The audio lives in memory, a few seconds of it at most; it is never written, and nothing it holds is logged.
/// </summary>
internal sealed class MicrophoneRouter : IVoiceInput, IDisposable
{
    // How much audio before the moment of decision a request starts with: the word itself, a little under a second, and the time the listener needs to be sure.
    private static readonly TimeSpan Lookback = TimeSpan.FromSeconds(1.6);

    // How much of the end of the seed is left in the request's words: the rest is the wake word, which the recognizer is told to leave out.
    private static readonly TimeSpan KeepOfSeed = TimeSpan.FromSeconds(0.35);

    // A request that nobody claimed is let go of after this long, and the wake word listens again.
    private static readonly TimeSpan HandoffExpiry = TimeSpan.FromSeconds(6);

    private readonly IMicrophoneAudioSource _source;
    private readonly ISpeechToTextService _recognizer;
    private readonly IWakeWordService _wakeWord;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly AudioRingBuffer _ring = new(VoiceAudio.SamplesIn(TimeSpan.FromSeconds(3)));
    private IMicrophoneAudioSubscription? _subscription;
    private IWakeWordSession? _wakeSession;
    private Listening? _listening;
    private WakeHandoff? _handoff;
    private DateTimeOffset? _lastRequestEndedAt;
    private bool _wakeWordOn;
    private bool _inputEnabled = true;
    private string? _unavailableMessage;
    private bool _disposed;

    public MicrophoneRouter(
        IMicrophoneAudioSource source, ISpeechToTextService recognizer, IWakeWordService wakeWord, TimeProvider? clock = null,
        ILogger<MicrophoneRouter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(wakeWord);
        _source = source;
        _recognizer = recognizer;
        _wakeWord = wakeWord;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MicrophoneRouter>.Instance;
    }

    /// <summary>
    /// Raised, on a background thread, when the wake word was heard and no request was being listened to. The request that follows claims the hand-off by
    /// passing it to <see cref="Listen"/>; one that is not claimed is let go of after a few seconds.
    /// </summary>
    public event EventHandler<WakeHandoff>? WakeWordHeard;

    /// <summary>Raised, on a background thread, when the microphone failed while only the wake word was using it.</summary>
    public event EventHandler<MicrophoneFailure>? WakeWordUnavailable;

    /// <summary>Whether the wake word is being listened for.</summary>
    public bool IsListeningForWakeWord
    {
        get
        {
            lock (_gate)
            {
                return _wakeWordOn && _subscription is not null && _wakeSession is not null;
            }
        }
    }

    /// <inheritdoc/>
    public VoiceEngineStatus RecognizerStatus => _recognizer.Status;

    public bool IsEnabled { get { lock (_gate) return _inputEnabled; } }

    /// <inheritdoc/>
    public string? UnavailableMessage { get { lock (_gate) return _inputEnabled ? null : _unavailableMessage; } }

    /// <param name="enabled">Whether a request may be listened to.</param>
    /// <param name="unavailableMessage">While it may not: why, when the reason is not that the user turned voice control off (game mode).</param>
    public void SetVoiceInputEnabled(bool enabled, string? unavailableMessage = null)
    {
        Listening? listening;
        lock (_gate)
        {
            _inputEnabled = enabled;
            _unavailableMessage = enabled ? null : unavailableMessage;
            listening = enabled ? null : _listening;
        }
        if (!enabled) SetWakeWord(false);
        listening?.Failed(MicrophoneFailure.Unavailable);
        listening?.Dispose();
    }

    /// <summary>When the last request stopped being listened to, or <see langword="null"/> if none has.</summary>
    public DateTimeOffset? LastRequestEndedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastRequestEndedAt;
            }
        }
    }

    /// <summary>Whether a request is being listened to.</summary>
    public bool IsListeningToRequest
    {
        get
        {
            lock (_gate)
            {
                return _listening is not null;
            }
        }
    }

    /// <summary>
    /// Turns listening for the wake word on or off. Off, the microphone closes unless a request is using it; on, it opens (in the background) and the
    /// listener's model is loaded. Turning it on again after the microphone failed tries the microphone again.
    /// </summary>
    public void SetWakeWord(bool on)
    {
        IWakeWordSession? toDispose = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            on &= _inputEnabled;
            _wakeWordOn = on;
            if (on)
            {
                if (_wakeSession is null)
                {
                    _wakeSession = _wakeWord.StartSession();
                    _wakeSession.Detected += OnWakeWord;
                }

                EnsureSubscription();
            }
            else
            {
                toDispose = _wakeSession;
                _wakeSession = null;
                _handoff = null;
                _ring.Clear();
                ReleaseIfIdle();
            }
        }

        toDispose?.Dispose();
    }

    /// <inheritdoc/>
    public IVoiceListening Listen(VoiceListenRequest request, Action<MicrophoneFailure> failed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failed);
        Listening? previous;
        Listening listening;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _listening;
            var handoff = request.Handoff is { } claimed && ReferenceEquals(claimed, _handoff) ? claimed : null;
            var options = new SpeechRecognitionOptions
            {
                TrailingSilence = request.TrailingSilence,
                InitialSilence = request.InitialSilence,
                IgnoreWordsBefore = handoff is null ? TimeSpan.Zero : Max(TimeSpan.Zero, handoff.SeedDuration - KeepOfSeed),
            };
            var session = _recognizer.StartSession(options);
            listening = new Listening(this, session, failed, stripWakeWord: handoff is not null);
            if (handoff is not null)
            {
                // The words around the wake word first, then everything the microphone heard while the request was being started, then the live audio.
                session.Push(handoff.Seed);
                foreach (var chunk in handoff.TakeAfter())
                {
                    session.Push(chunk);
                }

                _handoff = null;
            }

            _listening = listening;

            // While a request is heard, nothing is listened to for the wake word.
            _ring.Clear();
            _wakeSession?.Reset();
            EnsureSubscription();
        }

        previous?.Dispose();
        return listening;
    }

    /// <summary>Lets go of the microphone and both listeners.</summary>
    public void Dispose()
    {
        IWakeWordSession? wake;
        IMicrophoneAudioSubscription? subscription;
        Listening? listening;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            wake = _wakeSession;
            _wakeSession = null;
            subscription = _subscription;
            _subscription = null;
            listening = _listening;
            _listening = null;
        }

        listening?.Dispose();
        wake?.Dispose();
        subscription?.Dispose();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // Called with the lock held.
    private void EnsureSubscription()
    {
        if (_subscription is null && (_wakeWordOn || _listening is not null))
        {
            _subscription = _source.Subscribe(OnSamples, OnFailed);
        }
    }

    // Called with the lock held: the microphone closes when nobody needs it.
    private void ReleaseIfIdle()
    {
        if (_subscription is not null && !_wakeWordOn && _listening is null)
        {
            var subscription = _subscription;
            _subscription = null;

            // Disposing waits for nothing the capture thread holds this lock for, but it is done off the lock all the same.
            ThreadPool.QueueUserWorkItem(_ => subscription.Dispose());
        }
    }

    // One block of the microphone's audio, on the capture thread.
    private void OnSamples(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            if (_listening is { } listening)
            {
                listening.Push(samples);
                return;
            }

            if (_handoff is { } handoff)
            {
                handoff.Add(samples.ToArray());
                if (handoff.Elapsed > HandoffExpiry)
                {
                    // Nobody started the request: the wake word listens again.
                    _handoff = null;
                    _ring.Clear();
                    _wakeSession?.Reset();
                }

                return;
            }

            if (_wakeSession is { } wake)
            {
                _ring.Write(samples);
                wake.Push(samples);
            }
        }
    }

    // The microphone stopped working: a request that was listening is told, and the wake word is told that it is deaf until it is turned on again.
    private void OnFailed(MicrophoneFailure failure)
    {
        Listening? listening;
        bool wakeOnly;
        lock (_gate)
        {
            _subscription = null;
            _handoff = null;
            listening = _listening;
            wakeOnly = listening is null && _wakeWordOn;
        }

        listening?.Failed(failure);
        if (wakeOnly)
        {
            WakeWordUnavailable?.Invoke(this, failure);
        }
    }

    // The listener heard the word, on its own thread.
    private void OnWakeWord(object? sender, WakeWordDetectedEventArgs e)
    {
        WakeHandoff handoff;
        lock (_gate)
        {
            if (_listening is not null || _handoff is not null || _wakeSession is null)
            {
                return;
            }

            handoff = _handoff = new WakeHandoff(_ring.Last(VoiceAudio.SamplesIn(Lookback)));
        }

        WakeWordHeard?.Invoke(this, handoff);
    }

    private void EndListening(Listening listening)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_listening, listening))
            {
                return;
            }

            _listening = null;
            _lastRequestEndedAt = _clock.GetUtcNow();
            _ring.Clear();
            _wakeSession?.Reset();
            ReleaseIfIdle();
        }
    }

    // A request: its recognition session and the microphone's level.
    private sealed class Listening : IVoiceListening
    {
        private readonly MicrophoneRouter _router;
        private readonly ISpeechRecognitionSession _session;
        private readonly Action<MicrophoneFailure> _failed;
        private readonly bool _stripWakeWord;
        private int _disposed;

        public Listening(MicrophoneRouter router, ISpeechRecognitionSession session, Action<MicrophoneFailure> failed, bool stripWakeWord)
        {
            _router = router;
            _session = session;
            _failed = failed;
            _stripWakeWord = stripWakeWord;
            _session.Transcribed += (_, e) => Transcribed?.Invoke(this, _stripWakeWord ? Stripped(e) : e);
        }

        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

        public double Level
        {
            get
            {
                lock (_router._gate)
                {
                    return _router._subscription?.Level ?? 0;
                }
            }
        }

        public void Push(ReadOnlySpan<short> samples) => _session.Push(samples);

        public void Finish() => _session.Finish();

        public void Failed(MicrophoneFailure failure)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _failed(failure);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _router.EndListening(this);
            _session.Dispose();
        }

        // The recognizer may write what it heard of the wake word at the front of the request: it is taken off.
        private static SpeechTranscriptEventArgs Stripped(SpeechTranscriptEventArgs e) =>
            new(SpokenRequestText.StripWakeWord(e.Text), e.Ended);
    }
}
