using System.Collections.Concurrent;
using System.Diagnostics;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.Voice;

/// <summary>How <see cref="TextToSpeechService"/> keeps ahead of the speakers and looks after the engine's memory.</summary>
public sealed record TextToSpeechServiceOptions
{
    /// <summary>
    /// How much speech may be queued to the speakers before the voice stops making more. The text of an answer arrives faster than it is spoken, so without
    /// a limit the whole answer would be made at once, which wastes the processor if the user stops it a moment later.
    /// </summary>
    public TimeSpan MaxLookahead { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long an engine that is not kept loaded stays in memory after the last time it was used.</summary>
    public TimeSpan IdleUnloadAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the speakers' stream is kept open after the last sound, so that the next answer does not wait for the device.</summary>
    public TimeSpan StreamIdleClose { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The texts the benchmark speaks, short first: how an answer begins matters most.</summary>
    public IReadOnlyList<(string Name, string Text)> BenchmarkTexts { get; init; } =
    [
        ("Short reply", "Sure, I can help with that."),
        ("One sentence", "Your next meeting is at three o'clock tomorrow afternoon in the main conference room."),
        ("A few sentences", "You have two meetings tomorrow. The first is the design review at ten, and the second is a call with your brother about the exam schedule, which was moved to Friday morning."),
    ];
}

/// <summary>
/// The default <see cref="ITextToSpeechService"/> (PROJECT_SPEC §4.2, step 125). The answer's text is cut into pieces by a <see cref="SpeechSegmenter"/> as it arrives,
/// the pieces are queued, and one thread of its own makes each into speech with the chosen engine and hands the sound to the speakers as it is made; the
/// writer of the answer never waits for any of it. Everything that is said is only held in memory, and none of it is ever logged: the log has the engine's
/// identifier, counts and times.
/// </summary>
public sealed class TextToSpeechService : ITextToSpeechService
{
    private readonly ITextToSpeechEngineFactory _factory;
    private readonly IAudioOutput _output;
    private readonly TextToSpeechServiceOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    // The sound that is handed to the speakers, and the marks that say whether it may be: both change together, so a stopped response can never have a
    // late block slip through after the speakers were cleared.
    private readonly object _audio = new();
    private readonly object _gate = new();
    private readonly BlockingCollection<Piece> _queue = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _engineInUse = new(1, 1);
    private readonly Thread _worker;
    private readonly ITimer _idleTimer;

    private string _engineId = TextToSpeechModels.DefaultId;
    private ITextToSpeechEngine? _engine;
    private Task? _loading;
    private int _version;
    private VoiceEngineStatus _status = VoiceEngineStatus.Unloaded;
    private Response? _current;
    private IAudioOutputStream? _stream;
    private int _streamRate;
    private DateTimeOffset _lastUse;
    private DateTimeOffset _streamLastUsed;
    private bool _keepLoaded;
    private bool _wasSpeaking;
    private bool _isDisposed;

    /// <summary>Creates the service. Nothing is loaded and nothing is opened until it is needed.</summary>
    public TextToSpeechService(
        ITextToSpeechEngineFactory factory, IAudioOutput output, TextToSpeechServiceOptions? options = null, TimeProvider? clock = null,
        ILogger<TextToSpeechService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(output);
        _factory = factory;
        _output = output;
        _options = options ?? new TextToSpeechServiceOptions();
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TextToSpeechService>.Instance;
        _lastUse = _streamLastUsed = _clock.GetUtcNow();
        _worker = new Thread(Work) { IsBackground = true, Name = "Speech synthesis" };
        _worker.Start();
        _idleTimer = _clock.CreateTimer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc/>
    public event EventHandler? StatusChanged;

    /// <inheritdoc/>
    public event EventHandler? SpeakingChanged;

    /// <inheritdoc/>
    public TextToSpeechStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new TextToSpeechStatus(_engineId, _status);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsFinished: false };
            }
        }
    }

    /// <inheritdoc/>
    public bool KeepLoaded
    {
        get => Volatile.Read(ref _keepLoaded);
        set => Volatile.Write(ref _keepLoaded, value);
    }

    /// <inheritdoc/>
    public Task SelectEngineAsync(string engineId, bool load = true, CancellationToken cancellationToken = default)
    {
        var model = TextToSpeechModels.Find(engineId) ?? throw new ArgumentException("There is no such text-to-speech engine.", nameof(engineId));
        ITextToSpeechEngine? old = null;
        var change = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            change = !string.Equals(_engineId, model.Id, StringComparison.Ordinal);
            if (change)
            {
                _version++;
                old = _engine;
                _engine = null;
                _loading = null;
                _engineId = model.Id;
                _status = VoiceEngineStatus.Unloaded;
            }
        }

        if (change)
        {
            // Whatever is said stops before the engine that says it is let go of.
            StopAll();
            TextToSpeechLog.EngineChosen(_logger, model.Id);
            RaiseStatusChanged();
            Release(old);
        }

        return load ? WarmUpAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        var started = false;
        lock (_gate)
        {
            if (_isDisposed || _status.State is VoiceEngineState.Ready)
            {
                return Task.CompletedTask;
            }

            task = StartLoadLocked(out started);
        }

        if (started)
        {
            RaiseStatusChanged();
        }

        return task.WaitAsync(cancellationToken).ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        StopAll();
        ITextToSpeechEngine? old;
        Task? loading;
        lock (_gate)
        {
            _version++;
            old = _engine;
            loading = _loading;
            _engine = null;
            _loading = null;
            _status = VoiceEngineStatus.Unloaded;
        }
        if (loading is not null) await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _engineInUse.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { old?.Dispose(); }
        finally { _engineInUse.Release(); }
        RaiseStatusChanged();
    }

    /// <inheritdoc/>
    public ISpokenResponse BeginResponse()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        StopAll();
        var response = new Response(this);
        lock (_gate)
        {
            _current = response;
            _lastUse = _clock.GetUtcNow();
        }

        // The voice is wanted: the engine starts loading now if it is not loaded, while the model is still writing.
        _ = WarmUpAsync();
        RaiseSpeakingChanged();
        return response;
    }

    /// <inheritdoc/>
    public void StopAll()
    {
        Response? response;
        lock (_gate)
        {
            response = _current;
        }

        if (response is not null)
        {
            Stop(response);
        }
    }

    /// <inheritdoc/>
    public async Task<TextToSpeechBenchmarkResult> BenchmarkAsync(CancellationToken cancellationToken = default)
    {
        StopAll();
        string engineId;
        lock (_gate)
        {
            engineId = _engineId;
        }

        ITextToSpeechEngine? engine;
        try
        {
            engine = await AcquireEngineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (engine is null)
        {
            return new TextToSpeechBenchmarkResult(engineId, null, [], Status.Engine.Message ?? "The voice could not be loaded.");
        }

        var samples = new List<TextToSpeechSample>();
        TimeSpan? load;
        lock (_gate)
        {
            load = _status.LoadTime;
        }

        await _engineInUse.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (name, text) in _options.BenchmarkTexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleRate = engine.SampleRate;
                var total = 0L;
                TimeSpan? first = null;
                var timer = Stopwatch.StartNew();
                await Task.Run(
                    () => engine.Synthesize(
                        text,
                        block =>
                        {
                            first ??= timer.Elapsed;
                            total += block.Length;
                            return true;
                        },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                timer.Stop();
                samples.Add(new TextToSpeechSample(
                    name, text.Length, first ?? timer.Elapsed, timer.Elapsed, TimeSpan.FromSeconds(total / (double)Math.Max(1, sampleRate))));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TextToSpeechLog.BenchmarkFailed(_logger, engineId, exception.GetType().Name);
            return new TextToSpeechBenchmarkResult(engineId, load, samples, "The voice stopped while it was being measured.");
        }
        finally
        {
            _engineInUse.Release();
            lock (_gate)
            {
                _lastUse = _clock.GetUtcNow();
            }
        }

        var result = new TextToSpeechBenchmarkResult(engineId, load, samples);
        TextToSpeechLog.Benchmarked(_logger, engineId, (long)result.MedianTimeToFirstAudio.TotalMilliseconds, result.SpeedMultiple);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        ITextToSpeechEngine? engine;
        IAudioOutputStream? stream;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _version++;
            engine = _engine;
            _engine = null;
        }

        _idleTimer.Dispose();
        StopAll();
        _disposed.Cancel();
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        lock (_audio)
        {
            stream = _stream;
            _stream = null;
        }

        stream?.Dispose();
        engine?.Dispose();
        _disposed.Dispose();
        _engineInUse.Dispose();
    }

    // ---- loading ----

    // The load of the chosen engine that is under way, or a new one (on the thread pool, never under the lock). Called with the lock held.
    private Task StartLoadLocked(out bool started)
    {
        started = false;
        if (_loading is { } running)
        {
            return running;
        }

        var version = _version;
        var engineId = _engineId;
        _status = new VoiceEngineStatus(VoiceEngineState.Loading);
        started = true;
        return _loading = Task.Run(() => LoadAsync(version, engineId));
    }

    private async Task LoadAsync(int version, string engineId)
    {
        var model = TextToSpeechModels.Find(engineId)!;
        ITextToSpeechEngine? engine = null;
        var timer = Stopwatch.StartNew();
        try
        {
            engine = await Task.Run(() =>
            {
                var created = _factory.Create(model);
                try
                {
                    created.Load();
                }
                catch
                {
                    created.Dispose();
                    throw;
                }

                return created;
            }).ConfigureAwait(false);
        }
        catch (VoiceEngineException exception)
        {
            TextToSpeechLog.LoadFailed(_logger, engineId, exception.Failure);
            SetStatus(
                version,
                new VoiceEngineStatus(
                    exception.Failure is VoiceEngineFailure.NotInstalled or VoiceEngineFailure.FilesFailedCheck
                        ? VoiceEngineState.NotInstalled
                        : VoiceEngineState.Failed,
                    exception.Message));
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TextToSpeechLog.LoadFailed(_logger, engineId, VoiceEngineFailure.LoadFailed);
            TextToSpeechLog.LoadError(_logger, exception.GetType().Name);
            SetStatus(version, new VoiceEngineStatus(VoiceEngineState.Failed, "The voice could not be loaded."));
            return;
        }

        timer.Stop();
        bool keep;
        lock (_gate)
        {
            keep = !_isDisposed && version == _version;
            if (keep)
            {
                _engine = engine;
                _status = new VoiceEngineStatus(VoiceEngineState.Ready, null, timer.Elapsed);
                _lastUse = _clock.GetUtcNow();
            }
        }

        if (!keep)
        {
            // The user chose another engine while this one loaded.
            engine.Dispose();
            return;
        }

        TextToSpeechLog.Loaded(_logger, engineId, (long)timer.Elapsed.TotalMilliseconds);
        RaiseStatusChanged();
    }

    // Records a status for the engine of <version>, unless another engine has been chosen since.
    private void SetStatus(int version, VoiceEngineStatus status)
    {
        lock (_gate)
        {
            if (version != _version)
            {
                return;
            }

            _status = status;
            if (status.State is VoiceEngineState.Failed or VoiceEngineState.NotInstalled)
            {
                // A failed load is tried again the next time the engine is asked for, not remembered.
                _loading = null;
            }
        }

        RaiseStatusChanged();
    }

    // The engine, loaded: waits for the load that is under way, or starts one. Null when it cannot be loaded.
    private async Task<ITextToSpeechEngine?> AcquireEngineAsync(CancellationToken cancellationToken)
    {
        Task? loading;
        var started = false;
        lock (_gate)
        {
            if (_engine is { } ready)
            {
                return ready;
            }

            loading = StartLoadLocked(out started);
        }

        if (started)
        {
            RaiseStatusChanged();
        }

        await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _engine;
        }
    }

    private void Release(ITextToSpeechEngine? engine)
    {
        if (engine is null)
        {
            return;
        }

        // An engine that is making speech is not taken away from it: the synthesis was cancelled by StopAll and ends in a moment.
        _ = Task.Run(async () =>
        {
            try
            {
                await _engineInUse.WaitAsync(_disposed.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                engine.Dispose();
                return;
            }

            try
            {
                engine.Dispose();
            }
            finally
            {
                _engineInUse.Release();
            }
        });
    }

    private void CheckIdle()
    {
        ITextToSpeechEngine? engine = null;
        IAudioOutputStream? stream = null;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_engine is not null && _current is not { IsFinished: false } && !KeepLoaded && now - _lastUse >= _options.IdleUnloadAfter)
            {
                engine = _engine;
                _engine = null;
                _loading = null;
                _version++;
                _status = VoiceEngineStatus.Unloaded;
            }
        }

        lock (_audio)
        {
            if (_stream is not null && _current is not { IsFinished: false } && now - _streamLastUsed >= _options.StreamIdleClose)
            {
                stream = _stream;
                _stream = null;
            }
        }

        stream?.Dispose();
        if (engine is not null)
        {
            TextToSpeechLog.Unloaded(_logger, Status.EngineId);
            RaiseStatusChanged();
            Release(engine);
        }
    }

    // ---- speaking ----

    private enum PieceKind
    {
        Text,
        End,
    }

    private sealed record Piece(Response Response, PieceKind Kind, string Text);

    private void Enqueue(Response response, PieceKind kind, string text = "")
    {
        try
        {
            _queue.Add(new Piece(response, kind, text));
        }
        catch (InvalidOperationException)
        {
            // The service is gone: nothing is said.
        }
    }

    private void Work()
    {
        try
        {
            foreach (var piece in _queue.GetConsumingEnumerable(_disposed.Token))
            {
                var response = piece.Response;
                if (response.IsStopped)
                {
                    continue;
                }

                try
                {
                    if (piece.Kind == PieceKind.End)
                    {
                        FinishAfterPlayback(response);
                    }
                    else
                    {
                        Speak(response, piece.Text);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped, or another response took over.
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    TextToSpeechLog.SpeakFailed(_logger, Status.EngineId, exception.GetType().Name);
                    SetStatusOfCurrentEngineFailed();
                    Stop(response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The service was disposed.
        }
    }

    private void SetStatusOfCurrentEngineFailed()
    {
        int version;
        lock (_gate)
        {
            version = _version;
            var old = _engine;
            if (old is null)
            {
                return;
            }

            // An engine that failed while it was working is not trusted again: it is let go of and loads afresh the next time.
            _engine = null;
            _loading = null;
            _version++;
            version = _version;
            Release(old);
        }

        SetStatus(version, new VoiceEngineStatus(VoiceEngineState.Failed, "The voice stopped working. It will be loaded again the next time it is used."));
    }

    private void Speak(Response response, string text)
    {
        var token = response.Token;
        var engine = AcquireEngineAsync(token).GetAwaiter().GetResult();
        if (engine is null)
        {
            // It cannot be loaded (the status says why): the answer is not spoken, and the writer never knows.
            Stop(response);
            return;
        }

        var sampleRate = engine.SampleRate;
        IAudioOutputStream stream;
        try
        {
            lock (_audio)
            {
                if (response.IsStopped)
                {
                    return;
                }

                stream = OpenStream(sampleRate);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing to play on: the answer stays written, and the voice is not blamed.
            TextToSpeechLog.OutputUnavailable(_logger, exception.GetType().Name);
            Stop(response);
            return;
        }

        // The voice stays a little ahead of the speakers and no further.
        while (stream.Queued > _options.MaxLookahead)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(30);
        }

        _engineInUse.Wait(token);
        try
        {
            if (response.IsStopped)
            {
                return;
            }

            engine.Synthesize(
                text,
                samples =>
                {
                    lock (_audio)
                    {
                        if (response.IsStopped || _stream is null)
                        {
                            return false;
                        }

                        response.MarkAudio(_clock.GetTimestamp());
                        _stream.Write(samples);
                        _streamLastUsed = _clock.GetUtcNow();
                        return true;
                    }
                },
                token);
        }
        finally
        {
            _engineInUse.Release();
            lock (_gate)
            {
                _lastUse = _clock.GetUtcNow();
            }
        }
    }

    // The stream for <sampleRate>, opened when the first sound is wanted and kept for the next answer.
    private IAudioOutputStream OpenStream(int sampleRate)
    {
        if (_stream is not null && _streamRate == sampleRate)
        {
            return _stream;
        }

        _stream?.Dispose();
        _stream = _output.Open(sampleRate);
        _streamRate = sampleRate;
        return _stream;
    }

    // The end of an answer: finished once every piece has been made and played. Waiting here blocks later answers' pieces, but an answer that
    // takes over stops this one, which ends the wait.
    private void FinishAfterPlayback(Response response)
    {
        IAudioOutputStream? stream;
        lock (_audio)
        {
            stream = _stream;
        }

        if (stream is not null && response.HasAudio)
        {
            stream.WaitUntilDrainedAsync(response.Token).GetAwaiter().GetResult();
        }

        response.MarkFinished();
        lock (_gate)
        {
            if (ReferenceEquals(_current, response))
            {
                _current = null;
            }
        }

        RaiseSpeakingChanged();
    }

    // Stops one response: its sound goes quiet at once, what it has queued is dropped, and nothing more of it is said.
    private void Stop(Response response)
    {
        lock (_audio)
        {
            if (!response.TryStop())
            {
                return;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_current, response))
                {
                    _current = null;
                }
            }

            _stream?.Clear();
        }

        // Told about outside the locks: whoever listens may do anything.
        response.RaiseFinished();
        RaiseSpeakingChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseSpeakingChanged()
    {
        bool speaking;
        lock (_gate)
        {
            speaking = _current is { IsFinished: false };
            if (speaking == _wasSpeaking)
            {
                return;
            }

            _wasSpeaking = speaking;
        }

        SpeakingChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Response : ISpokenResponse
    {
        private readonly TextToSpeechService _owner;
        private readonly SpeechSegmenter _segmenter = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly object _lock = new();
        private long _startedAt;
        private long _firstAudioAt;
        private int _state;
        private bool _completed;
        private bool _hasAudio;

        public Response(TextToSpeechService owner)
        {
            _owner = owner;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public CancellationToken Token => _cancel.Token;

        public bool IsStopped => Volatile.Read(ref _state) == 2;

        public bool IsFinished => Volatile.Read(ref _state) != 0;

        public bool HasAudio => Volatile.Read(ref _hasAudio);

        public TimeSpan? TimeToFirstAudio
        {
            get
            {
                var started = Volatile.Read(ref _startedAt);
                var first = Volatile.Read(ref _firstAudioAt);
                return started != 0 && first != 0 ? _owner._clock.GetElapsedTime(started, first) : null;
            }
        }

        public event EventHandler? Finished;

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text) || IsFinished)
            {
                return;
            }

            IReadOnlyList<string> pieces;
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                Interlocked.CompareExchange(ref _startedAt, _owner._clock.GetTimestamp(), 0);
                pieces = _segmenter.Append(text);
            }

            foreach (var piece in pieces)
            {
                _owner.Enqueue(this, PieceKind.Text, piece);
            }
        }

        public void Complete()
        {
            IReadOnlyList<string> rest;
            lock (_lock)
            {
                if (_completed || IsFinished)
                {
                    return;
                }

                _completed = true;
                rest = _segmenter.Complete();
            }

            foreach (var piece in rest)
            {
                _owner.Enqueue(this, PieceKind.Text, piece);
            }

            _owner.Enqueue(this, PieceKind.End);
        }

        public void Stop() => _owner.Stop(this);

        public void MarkAudio(long timestamp)
        {
            Volatile.Write(ref _hasAudio, true);
            Interlocked.CompareExchange(ref _firstAudioAt, timestamp, 0);
        }

        // Stopped, once: false when the response was already over.
        public bool TryStop()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            {
                return false;
            }

            _cancel.Cancel();
            return true;
        }

        public void RaiseFinished() => Finished?.Invoke(this, EventArgs.Empty);

        public void MarkFinished()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                RaiseFinished();
            }
        }
    }
}

/// <summary>Text-to-speech log messages. They name the engine and give counts and times, never anything that was said.</summary>
internal static partial class TextToSpeechLog
{
    [LoggerMessage(EventId = 9100, Level = LogLevel.Information, Message = "Text-to-speech engine chosen: {EngineId}")]
    public static partial void EngineChosen(ILogger logger, string engineId);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Text-to-speech engine {EngineId} loaded in {LoadMs} ms")]
    public static partial void Loaded(ILogger logger, string engineId, long loadMs);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "Text-to-speech engine {EngineId} could not be loaded ({Failure})")]
    public static partial void LoadFailed(ILogger logger, string engineId, VoiceEngineFailure failure);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Warning, Message = "Text-to-speech engine failed to load: {ExceptionType}")]
    public static partial void LoadError(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Warning, Message = "Text-to-speech engine {EngineId} failed while speaking: {ExceptionType}")]
    public static partial void SpeakFailed(ILogger logger, string engineId, string exceptionType);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Information, Message = "Text-to-speech engine {EngineId} let go of after a while without use")]
    public static partial void Unloaded(ILogger logger, string engineId);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Information, Message = "Text-to-speech engine {EngineId} measured: first audio {FirstAudioMs} ms (median), {SpeedMultiple:F1}x real time")]
    public static partial void Benchmarked(ILogger logger, string engineId, long firstAudioMs, double speedMultiple);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Text-to-speech engine {EngineId} could not be measured: {ExceptionType}")]
    public static partial void BenchmarkFailed(ILogger logger, string engineId, string exceptionType);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Speech could not be played: {ExceptionType}")]
    public static partial void OutputUnavailable(ILogger logger, string exceptionType);
}
