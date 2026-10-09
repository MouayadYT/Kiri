using System.Threading.Channels;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Loads and unloads the local model through the model host, starting the host when the first load needs it
/// (PROJECT_SPEC §5.6), and tells the rest of the app the model's status.
/// </summary>
/// <remarks>
/// <para>
/// The status comes from the host: it reports every change as a <see cref="ModelStatusReport"/>, and this class
/// publishes each newer one as a <see cref="ModelStatusChanged"/>. It adds only the statuses that exist before the
/// host does or after it is gone: loading while the host starts, and failed when the host cannot be started or exits.
/// The host sends each report before the reply that caused it, so once a load or unload returns, <see cref="Current"/>
/// already shows its result.
/// </para>
/// <para>
/// Generating goes through the same connection (<see cref="GenerateAsync"/>), so an answer ends when the host does.
/// </para>
/// <para>Logs carry statuses, reasons and durations only, never a path or a model name.</para>
/// </remarks>
public sealed partial class ModelLifecycle : IModelLifecycle, ILocalAiPause, IAsyncDisposable
{
    private readonly IModelHostLauncher _launcher;
    private readonly IAppEventBus _events;
    private readonly ILogger<ModelLifecycle> _logger;

    // Starting the host is done one caller at a time, but a request to it is not, so a later load can replace one that
    // is going.
    private readonly SemaphoreSlim _hostGate = new(1, 1);
    private readonly Channel<ModelStatusChanged> _published = Channel.CreateUnbounded<ModelStatusChanged>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _publisher;

    // _sync guards the status, the loaded model and the connection.
    private readonly object _sync = new();
    private ModelStatusChanged _current = new(ModelStatus.NotLoaded);
    private ModelInfo? _model;
    private Connection? _connection;
    private bool _disposed;
    private LocalAiPauseReason _pauseReason;

    /// <summary>Creates a lifecycle that starts hosts with <paramref name="launcher"/> and publishes on <paramref name="events"/>.</summary>
    public ModelLifecycle(IModelHostLauncher launcher, IAppEventBus events, ILogger<ModelLifecycle> logger)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _launcher = launcher;
        _events = events;
        _logger = logger;
        _publisher = Task.Run(PublishAsync);
    }

    /// <inheritdoc/>
    public ModelStatusChanged Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public ModelInfo? Model
    {
        get
        {
            lock (_sync)
            {
                // The status says whether the model the last load returned is still the one that is ready.
                return _model is { } model && _current.Status == ModelStatus.Ready && _current.ModelId == model.Id
                    ? model
                    : null;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsPaused => Reason != LocalAiPauseReason.None;

    /// <inheritdoc/>
    public LocalAiPauseReason Reason
    {
        get
        {
            lock (_sync)
            {
                return _pauseReason;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public Task PauseAsync(CancellationToken cancellationToken = default) => PauseAsync(LocalAiPauseReason.User, cancellationToken);

    /// <inheritdoc/>
    public async Task PauseAsync(LocalAiPauseReason reason, CancellationToken cancellationToken = default)
    {
        if (reason == LocalAiPauseReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "A pause needs a reason.");
        }

        lock (_sync)
        {
            if (_pauseReason != LocalAiPauseReason.None)
            {
                return;
            }

            _pauseReason = reason;
        }

        LogPaused(_logger, reason);
        Changed?.Invoke(this, EventArgs.Empty);

        // Whatever is loaded, or loading, goes: the paused state is already set, so nothing starts a load in the meantime.
        try
        {
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelHostException)
        {
            // The host is gone or already replaced what was loading; either way nothing is left loaded.
        }
    }

    /// <inheritdoc/>
    public void Resume()
    {
        lock (_sync)
        {
            if (_pauseReason == LocalAiPauseReason.None)
            {
                return;
            }

            _pauseReason = LocalAiPauseReason.None;
        }

        LogResumed(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task<ModelInfo> LoadAsync(ModelFiles files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ThrowIfPaused();
        var modelId = files.DeriveModelId();
        var connection = await EnsureHostAsync(modelId, cancellationToken).ConfigureAwait(false);
        try
        {
            // Starting the host takes a moment, and the user may have paused the local AI while it did: the status it was given for the
            // start is taken back.
            if (IsPaused)
            {
                Apply(new ModelStatusChanged(ModelStatus.NotLoaded));
                ThrowIfPaused();
            }

            var model = await connection.Client
                .LoadModelAsync(new LoadModelRequest(modelId) { Files = files }, null, cancellationToken)
                .ConfigureAwait(false);

            // The same while the model loaded, if the pause's own unload came before the host had it.
            if (IsPaused)
            {
                await TryUnloadAsync(connection).ConfigureAwait(false);
                ThrowIfPaused();
            }

            lock (_sync)
            {
                if (_connection == connection)
                {
                    _model = model;
                }
            }

            return model;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller no longer wants the model, so the load is ended rather than left running.
            await TryUnloadAsync(connection).ConfigureAwait(false);
            throw;
        }
        catch (ModelHostException exception) when (exception.Code == ModelHostErrorCode.Cancelled && Paused(Reason) is { } paused)
        {
            // The pause's unload replaced this load; the local AI was paused, so that is what is said.
            throw paused;
        }
    }

    /// <inheritdoc/>
    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        Connection? connection;
        lock (_sync)
        {
            connection = _connection;
        }

        // Without a host there is nothing loaded.
        if (connection is not null)
        {
            await connection.Client.UnloadModelAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ModelHostReply> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Connection? connection;
        lock (_sync)
        {
            connection = _connection;
        }

        return connection is not null
            ? connection.Client.GenerateAsync(request, cancellationToken)
            : throw new ModelHostException(ModelHostErrorCode.ModelNotFound, "No model host is running, so no model is loaded.");
    }

    /// <summary>Ends the model host, which unloads the model, and stops publishing.</summary>
    public async ValueTask DisposeAsync()
    {
        Connection? connection;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        _published.Writer.TryComplete();
        await _publisher.ConfigureAwait(false);
        if (connection is not null)
        {
            connection.Client.ModelStatusReported -= connection.OnReport;
            await connection.Host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfPaused()
    {
        if (Paused(Reason) is { } paused)
        {
            throw paused;
        }
    }

    // What a load is refused with while the local AI is paused for this reason: the code says who paused it, so the user is told how to get it back.
    private static ModelHostException? Paused(LocalAiPauseReason reason) => reason switch
    {
        LocalAiPauseReason.None => null,
        LocalAiPauseReason.GameMode => new ModelHostException(ModelHostErrorCode.PausedForGame, "Game mode has paused the local AI."),
        _ => new ModelHostException(ModelHostErrorCode.Paused, "The local AI is paused."),
    };

    private async Task<Connection> EnsureHostAsync(string modelId, CancellationToken cancellationToken)
    {
        await _hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connection is { } running)
                {
                    return running;
                }
            }

            // Starting the host takes a moment, which is part of loading the model.
            Apply(new ModelStatusChanged(ModelStatus.Loading) { ModelId = modelId });
            IModelHostConnection host;
            try
            {
                host = await _launcher.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Apply(new ModelStatusChanged(ModelStatus.NotLoaded));
                throw;
            }
            catch (ModelHostException)
            {
                Apply(new ModelStatusChanged(ModelStatus.Failed) { ModelId = modelId, Failure = ModelFailure.HostUnavailable });
                throw;
            }

            var connection = new Connection(host, this);
            lock (_sync)
            {
                _connection = connection;
            }

            host.Client.ModelStatusReported += connection.OnReport;
            _ = WatchAsync(connection);
            return connection;
        }
        finally
        {
            _hostGate.Release();
        }
    }

    // The host reports a change before it answers the request that caused it.
    private void OnReport(Connection connection, ModelStatusReport report)
    {
        lock (_sync)
        {
            if (_connection != connection || report.Sequence <= connection.LastSequence)
            {
                return;
            }

            connection.LastSequence = report.Sequence;
            ApplyLocked(new ModelStatusChanged(report.Status) { ModelId = report.ModelId, Failure = report.Failure });
        }
    }

    // When the host goes away its engine and the model go with it.
    private async Task WatchAsync(Connection connection)
    {
        try
        {
            await connection.Client.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogWatchFailed(_logger, exception);
        }

        var lost = false;
        lock (_sync)
        {
            if (_connection == connection)
            {
                _connection = null;
                lost = true;
                var status = _current.Status;
                ApplyLocked(status is ModelStatus.Loading or ModelStatus.Ready or ModelStatus.Unloading
                    ? new ModelStatusChanged(ModelStatus.Failed) { ModelId = _current.ModelId, Failure = ModelFailure.HostUnavailable }
                    : new ModelStatusChanged(status) { ModelId = _current.ModelId, Failure = _current.Failure });
            }
        }

        if (lost)
        {
            LogHostLost(_logger);
            connection.Client.ModelStatusReported -= connection.OnReport;
            await connection.Host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task TryUnloadAsync(Connection connection)
    {
        try
        {
            await connection.Client.UnloadModelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModelHostException)
        {
            // The host is gone or already replaced the load; either way nothing is left loading.
        }
    }

    private void Apply(ModelStatusChanged status)
    {
        lock (_sync)
        {
            ApplyLocked(status);
        }
    }

    // Must be called with _sync held.
    private void ApplyLocked(ModelStatusChanged status)
    {
        if (_current == status)
        {
            return;
        }

        _current = status;
        LogStatus(_logger, status.Status, status.Failure);
        _published.Writer.TryWrite(status);
    }

    private async Task PublishAsync()
    {
        await foreach (var status in _published.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _events.PublishAsync(status).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPublishFailed(_logger, exception);
            }
        }
    }

    private sealed class Connection(IModelHostConnection host, ModelLifecycle owner)
    {
        public IModelHostConnection Host { get; } = host;

        public ModelHostClient Client => Host.Client;

        public long LastSequence { get; set; }

        public void OnReport(object? sender, ModelStatusReport report) => owner.OnReport(this, report);
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Local model status is {Status} ({Failure})")]
    private static partial void LogStatus(ILogger logger, ModelStatus status, ModelFailure? failure);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "The model host went away, taking the model with it")]
    private static partial void LogHostLost(ILogger logger);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Error, Message = "Watching the model host failed")]
    private static partial void LogWatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2113, Level = LogLevel.Error, Message = "A subscriber to the model status failed")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2114, Level = LogLevel.Information, Message = "The local AI was paused ({Reason})")]
    private static partial void LogPaused(ILogger logger, LocalAiPauseReason reason);

    [LoggerMessage(EventId = 2115, Level = LogLevel.Information, Message = "The local AI was resumed")]
    private static partial void LogResumed(ILogger logger);
}
