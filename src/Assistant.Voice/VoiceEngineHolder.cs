using System.Diagnostics;
using Assistant.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Assistant.Voice;

/// <summary>
/// Holds one engine that is read into memory when it is first wanted, shared by the sessions that use it, and let go of when nothing uses it (PROJECT_SPEC
/// §4.2, step 125): the speech recognizer and the wake-word listener each keep one. Loading happens on the thread pool and at most once at a time; a
/// load that failed is tried again the next time the engine is wanted.
/// </summary>
internal sealed class VoiceEngineHolder<T> : IDisposable
    where T : class, IDisposable
{
    private readonly Func<T> _load;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly object _gate = new();
    private T? _engine;
    private Task? _loading;
    private VoiceEngineStatus _status = VoiceEngineStatus.Unloaded;
    private int _users;
    private bool _unloadWhenIdle;
    private bool _disposed;

    public VoiceEngineHolder(Func<T> load, ILogger logger, string name)
    {
        _load = load;
        _logger = logger;
        _name = name;
    }

    public event EventHandler? StatusChanged;

    public VoiceEngineStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts loading if the engine is not loaded and not loading; completes when it is loaded or has failed. Never throws.</summary>
    public Task WarmUpAsync()
    {
        Task task;
        var started = false;
        lock (_gate)
        {
            if (_disposed || _engine is not null)
            {
                return Task.CompletedTask;
            }

            if (_loading is null)
            {
                _unloadWhenIdle = false;
                _status = new VoiceEngineStatus(VoiceEngineState.Loading);
                _loading = Task.Run(LoadNow);
                started = true;
            }

            task = _loading;
        }

        if (started)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        return task;
    }

    /// <summary>
    /// The loaded engine, for a session that is about to use it: waits for it to load, loading it if need be. <see langword="null"/> when it cannot be
    /// loaded. Every engine this returns must be given back with <see cref="Release"/>. Blocking: call it off the UI thread.
    /// </summary>
    public T? Acquire(CancellationToken cancellationToken = default)
    {
        try
        {
            WarmUpAsync().Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        lock (_gate)
        {
            if (_engine is null || _disposed)
            {
                return null;
            }

            _users++;
            return _engine;
        }
    }

    /// <summary>Gives back an engine that <see cref="Acquire"/> returned.</summary>
    public void Release()
    {
        T? toDispose = null;
        lock (_gate)
        {
            _users = Math.Max(0, _users - 1);
            if (_users == 0 && _unloadWhenIdle)
            {
                toDispose = TakeEngineLocked();
            }
        }

        Dispose(toDispose);
    }

    /// <summary>Lets go of the engine now, or, while sessions use it, as soon as the last of them is done.</summary>
    public void Unload()
    {
        T? toDispose = null;
        lock (_gate)
        {
            if (_users > 0)
            {
                _unloadWhenIdle = true;
                return;
            }

            toDispose = TakeEngineLocked();
        }

        Dispose(toDispose);
    }

    public void Dispose()
    {
        T? toDispose;
        lock (_gate)
        {
            _disposed = true;
            toDispose = TakeEngineLocked();
        }

        Dispose(toDispose);
    }

    private T? TakeEngineLocked()
    {
        var engine = _engine;
        _engine = null;
        _loading = null;
        _unloadWhenIdle = false;
        if (engine is not null)
        {
            _status = VoiceEngineStatus.Unloaded;
        }

        return engine;
    }

    private void Dispose(T? engine)
    {
        if (engine is null)
        {
            return;
        }

        engine.Dispose();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadNow()
    {
        var timer = Stopwatch.StartNew();
        VoiceEngineStatus result;
        T? loaded = null;
        try
        {
            loaded = _load();
            timer.Stop();
            result = new VoiceEngineStatus(VoiceEngineState.Ready, null, timer.Elapsed);
            VoiceLog.Loaded(_logger, _name, (long)timer.Elapsed.TotalMilliseconds);
        }
        catch (VoiceEngineException exception)
        {
            VoiceLog.LoadFailed(_logger, _name, exception.Failure);
            result = new VoiceEngineStatus(
                exception.Failure is VoiceEngineFailure.NotInstalled or VoiceEngineFailure.FilesFailedCheck ? VoiceEngineState.NotInstalled : VoiceEngineState.Failed,
                exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            VoiceLog.LoadFailed(_logger, _name, VoiceEngineFailure.LoadFailed);
            VoiceLog.LoadError(_logger, _name, exception.GetType().Name);
            result = new VoiceEngineStatus(VoiceEngineState.Failed, "It could not be loaded from its files.");
        }

        var keep = false;
        lock (_gate)
        {
            if (!_disposed && loaded is not null)
            {
                _engine = loaded;
                keep = true;
            }
            else
            {
                // A failed load is forgotten, so the next request tries again.
                _loading = null;
            }

            _status = result;
        }

        if (!keep)
        {
            loaded?.Dispose();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Voice engine log messages. They name the engine and give counts and times, never audio or words.</summary>
internal static partial class VoiceLog
{
    [LoggerMessage(EventId = 9110, Level = LogLevel.Information, Message = "Voice engine {Engine} loaded in {LoadMs} ms")]
    public static partial void Loaded(ILogger logger, string engine, long loadMs);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Warning, Message = "Voice engine {Engine} could not be loaded ({Failure})")]
    public static partial void LoadFailed(ILogger logger, string engine, VoiceEngineFailure failure);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Warning, Message = "Voice engine {Engine} failed to load: {ExceptionType}")]
    public static partial void LoadError(ILogger logger, string engine, string exceptionType);

    [LoggerMessage(EventId = 9113, Level = LogLevel.Warning, Message = "Voice engine {Engine} failed while working: {ExceptionType}")]
    public static partial void Failed(ILogger logger, string engine, string exceptionType);

    [LoggerMessage(EventId = 9114, Level = LogLevel.Information, Message = "The wake word was heard")]
    public static partial void WakeWordHeard(ILogger logger);

    [LoggerMessage(EventId = 9115, Level = LogLevel.Information, Message = "A word that sounded like the wake word was ignored ({Reason})")]
    public static partial void WakeWordIgnored(ILogger logger, string reason);

    [LoggerMessage(EventId = 9116, Level = LogLevel.Information,
        Message = "The wake word listener heard {Seconds} s, {LoudSeconds} s of it above the noise, loudest {Loudest} %")]
    public static partial void WakeWordListening(ILogger logger, long seconds, long loudSeconds, long loudest);
}
