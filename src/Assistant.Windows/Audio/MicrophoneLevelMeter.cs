using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Audio;

/// <summary>
/// Measures the default microphone for voice-input feedback and shares its audio with whatever else needs it: speech recognition and the wake-word
/// listener (PROJECT_SPEC §4.2, step 125). There is one capture, on its own background thread, for as long as anyone is subscribed, so opening a device
/// never blocks the caller and a second subscriber never opens a second one. Each block of samples is reduced to a level straight away and handed to
/// the subscribers that want the audio; nothing is recorded, stored or logged here.
/// </summary>
public sealed class MicrophoneLevelMeter : IMicrophoneLevelMeter, IMicrophoneAudioSource
{
    private readonly ILogger<MicrophoneLevelMeter> _logger;
    private readonly Func<IMicrophoneCapture> _createCapture;
    private readonly object _gate = new();
    private Capture? _current;

    public MicrophoneLevelMeter(ILogger<MicrophoneLevelMeter> logger, MicrophoneChoice? choice = null)
        : this(logger, MakeCapture(choice ?? new MicrophoneChoice())) { }

    private static Func<IMicrophoneCapture> MakeCapture(MicrophoneChoice choice) => () => new WasapiMicrophoneCapture(() => choice.DeviceId);

    internal MicrophoneLevelMeter(ILogger<MicrophoneLevelMeter> logger, Func<IMicrophoneCapture> createCapture)
    {
        _logger = logger;
        _createCapture = createCapture;
    }

    /// <inheritdoc/>
    public IMicrophoneLevelSession Start(Action<MicrophoneFailure> failed) => Subscribe(null, failed);

    /// <inheritdoc/>
    public IMicrophoneAudioSubscription Subscribe(MicrophoneAudioHandler? onSamples, Action<MicrophoneFailure> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        lock (_gate)
        {
            while (true)
            {
                // A capture that is closing is let finish: the new subscriber gets a capture of its own, never half of an old one.
                var isNew = _current is null || _current.IsClosing;
                if (isNew)
                {
                    _current = new Capture(_createCapture(), _logger);
                }

                var subscription = new Subscription(this, _current!, onSamples, failed);
                if (_current!.Add(subscription))
                {
                    // The microphone is opened only once its first subscriber is listed, so a device that fails at once still tells it.
                    if (isNew)
                    {
                        _current.Start();
                    }

                    return subscription;
                }

                // The capture ended between the look and the add (it failed on its own thread); try again with a fresh one.
                _current = null;
            }
        }
    }

    private void Remove(Capture capture, Subscription subscription)
    {
        lock (_gate)
        {
            if (capture.Remove(subscription) && ReferenceEquals(_current, capture))
            {
                _current = null;
            }
        }
    }

    private sealed class Subscription : IMicrophoneAudioSubscription
    {
        private readonly MicrophoneLevelMeter _owner;
        private readonly Capture _capture;
        private readonly Action<MicrophoneFailure> _failed;
        private volatile bool _disposed;

        public Subscription(MicrophoneLevelMeter owner, Capture capture, MicrophoneAudioHandler? handler, Action<MicrophoneFailure> failed)
        {
            _owner = owner;
            _capture = capture;
            Handler = handler;
            _failed = failed;
        }

        public MicrophoneAudioHandler? Handler { get; }

        public double Level => _disposed ? 0 : _capture.Level;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Remove(_capture, this);
        }

        // A capture that has failed tells each subscription that is still listening, once.
        public void Fail(MicrophoneFailure failure)
        {
            if (!_disposed)
            {
                _failed(failure);
            }
        }
    }

    // One open microphone, with the subscriptions that are listening to it.
    private sealed class Capture
    {
        private readonly ManualResetEvent _stop = new(false);
        private readonly object _lock = new();
        private readonly ILogger _logger;
        private Subscription[] _subscriptions = [];
        private volatile float _level;
        private bool _closing;
        private bool _exited;

        private readonly IMicrophoneCapture _capture;

        public Capture(IMicrophoneCapture capture, ILogger logger)
        {
            _capture = capture;
            _logger = logger;
        }

        // Opens the microphone on a thread of its own.
        public void Start() => new Thread(() => Run(_capture)) { IsBackground = true, Name = "Microphone" }.Start();

        public bool IsClosing
        {
            get
            {
                lock (_lock)
                {
                    return _closing;
                }
            }
        }

        public double Level => _level;

        // False when the capture has already ended, so the subscription would never be told how it went.
        public bool Add(Subscription subscription)
        {
            lock (_lock)
            {
                if (_closing)
                {
                    return false;
                }

                _subscriptions = [.. _subscriptions, subscription];
                return true;
            }
        }

        // Takes a subscription away; true when it was the last, which closes the microphone.
        public bool Remove(Subscription subscription)
        {
            lock (_lock)
            {
                _subscriptions = [.. _subscriptions.Where(known => !ReferenceEquals(known, subscription))];
                if (_subscriptions.Length > 0 || _closing)
                {
                    return false;
                }

                _closing = true;
                if (_exited)
                {
                    _stop.Dispose();
                }
                else
                {
                    _stop.Set();
                }

                return true;
            }
        }

        private void Run(IMicrophoneCapture capture)
        {
            var started = Stopwatch.GetTimestamp();
            MicrophoneLog.Opening(_logger);
            MicrophoneFailure? failure = null;
            try
            {
                capture.Run(Deliver, _stop);
                MicrophoneLog.Closed(_logger, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (MicrophoneException exception)
            {
                MicrophoneLog.Failed(_logger, exception.Failure, exception.ErrorCode);
                failure = exception.Failure;
            }
            catch (Exception exception)
            {
                MicrophoneLog.Error(_logger, exception);
                failure = MicrophoneFailure.Unavailable;
            }

            _level = 0;
            Subscription[] listening;
            lock (_lock)
            {
                _exited = true;

                // A capture that ends by itself has failed; one that was asked to stop has not.
                listening = _closing ? [] : _subscriptions;
                if (_closing)
                {
                    _stop.Dispose();
                }
                else
                {
                    // Nobody can get this capture any more: its subscribers are told, and a new subscription opens the microphone afresh.
                    _closing = true;
                    _subscriptions = [];
                }
            }

            foreach (var subscription in listening)
            {
                subscription.Fail(failure ?? MicrophoneFailure.Unavailable);
            }
        }

        // One block of samples, on the capture thread: its level, and the audio for whoever wants it.
        private void Deliver(ReadOnlySpan<short> samples)
        {
            _level = (float)AudioLevel.Rms(samples);
            foreach (var subscription in Volatile.Read(ref _subscriptions))
            {
                subscription.Handler?.Invoke(samples);
            }
        }
    }
}
