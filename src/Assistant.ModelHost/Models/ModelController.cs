using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Processes;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Models;

/// <summary>
/// Loads and unloads the model by starting and stopping the engine (<see cref="IModelProcessManager"/>), and turns what
/// happens into the status the app shows (<see cref="ModelStatus"/>).
/// </summary>
/// <remarks>
/// <para>
/// Loads and unloads take turns. A new one first stops the one still going, which is then answered with
/// <see cref="ModelHostErrorCode.Cancelled"/>, so "unload" always ends a load and a second "load" replaces the first.
/// While one of them runs it sets the status itself: <see cref="ModelStatus.Loading"/> then
/// <see cref="ModelStatus.Ready"/> or <see cref="ModelStatus.Failed"/>, or <see cref="ModelStatus.Unloading"/> then
/// <see cref="ModelStatus.NotLoaded"/>. Between them the status follows the engine, which the manager restarts after an
/// unexpected exit: loading again while it restarts, ready when it is back, failed when it gives up.
/// </para>
/// <para>
/// Neither the status nor the log holds a path: only the model's identifier, which the owner chose, and reasons.
/// </para>
/// </remarks>
internal sealed class ModelController : IModelController, IDisposable
{
    private readonly IModelProcessManager _engine;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // _sync guards everything below it, and status changes are raised while it is held so that they leave in order.
    private readonly object _sync = new();
    private CancellationTokenSource? _newest;
    private int _operations;
    private ModelStatusReport _report = new(0, ModelStatus.NotLoaded);
    private ModelInfo? _model;

    public ModelController(IModelProcessManager engine, TimeProvider timeProvider, ILogger<ModelController> logger)
    {
        _engine = engine;
        _timeProvider = timeProvider;
        _logger = logger;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public event EventHandler<ModelStatusReport>? Changed;

    public ModelStatusReport Current
    {
        get
        {
            lock (_sync)
            {
                return _report;
            }
        }
    }

    public ModelInfo? Model
    {
        get
        {
            lock (_sync)
            {
                return _model;
            }
        }
    }

    public async Task<ModelInfo> LoadAsync(LoadModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // There is no catalog to look an identifier up in yet, so only files can be loaded.
        var files = request.Files
            ?? throw new ModelRequestException(ModelHostErrorCode.ModelNotFound, ModelFailure.ModelNotFound);
        var operation = Begin(cancellationToken);
        var start = _timeProvider.GetTimestamp();
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    _model = null;
                    SetStatus(ModelStatus.Loading, request.ModelId, null);
                }

                return await LoadCoreAsync(request.ModelId, files, start, operation.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A later load or unload took over; it reports the status from here.
            ModelControllerLog.LoadSuperseded(_logger);
            throw new ModelRequestException(ModelHostErrorCode.Cancelled);
        }
        finally
        {
            End(operation);
        }
    }

    public async Task<string?> UnloadAsync(CancellationToken cancellationToken)
    {
        var operation = Begin(cancellationToken);
        var start = _timeProvider.GetTimestamp();
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                string? modelId;
                lock (_sync)
                {
                    modelId = _report.ModelId;
                    if (_report.Status == ModelStatus.NotLoaded && _engine.State == ModelProcessState.Stopped)
                    {
                        return null;
                    }

                    SetStatus(ModelStatus.Unloading, modelId, null);
                }

                await _engine.StopAsync(operation.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    _model = null;
                    SetStatus(ModelStatus.NotLoaded, null, null);
                }

                ModelControllerLog.Unloaded(_logger, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds);
                return modelId;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelRequestException(ModelHostErrorCode.Cancelled);
        }
        finally
        {
            End(operation);
        }
    }

    public void Dispose() => _engine.StateChanged -= OnEngineStateChanged;

    private async Task<ModelInfo> LoadCoreAsync(
        string modelId,
        ModelFiles files,
        long start,
        CancellationToken cancellationToken)
    {
        try
        {
            ModelFileCheck.Verify(files);
            var launch = new ModelProcessLaunch(files.ModelPath)
            {
                ProjectorPath = files.ProjectorPath,
                ChatTemplatePath = files.ChatTemplatePath,
                ContextLength = files.ContextLength,
                RuntimeArguments = files.RuntimeArguments,

                // No devices listed is the processor alone; null leaves it to the engine's own choice of the roomiest devices.
                Devices = !files.UseGpu ? [] : files.GpuDeviceId is { } device ? [device] : null,
            };
            await _engine.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelRequestException failed)
        {
            throw Fail(modelId, failed);
        }
        catch (ModelProcessException failed)
        {
            throw Fail(modelId, new ModelRequestException(CodeOf(failed.Failure), FailureOf(failed.Failure)));
        }

        var contextLength = _engine.ContextLength ?? files.ContextLength ?? ModelProcessLaunch.DefaultContextLength;
        var model = new ModelInfo(modelId, contextLength)
        {
            SupportsVision = files.ProjectorPath is not null,
            SupportsConstrainedOutput = true,
            SupportsToolCalling = true,
        };
        lock (_sync)
        {
            _model = model;
            SetStatus(ModelStatus.Ready, modelId, null);
        }

        ModelControllerLog.Loaded(
            _logger, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds, contextLength, model.SupportsVision);
        return model;
    }

    private ModelRequestException Fail(string modelId, ModelRequestException failed)
    {
        var failure = failed.Failure ?? ModelFailure.LoadFailed;
        ModelControllerLog.LoadFailed(_logger, failure);
        lock (_sync)
        {
            _model = null;
            SetStatus(ModelStatus.Failed, modelId, failure);
        }

        return failed;
    }

    // Starts an operation, which supersedes the one before it.
    private CancellationTokenSource Begin(CancellationToken cancellationToken)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            previous = _newest;
            _newest = operation;
            _operations++;
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended while this one began.
        }

        return operation;
    }

    private void End(CancellationTokenSource operation)
    {
        lock (_sync)
        {
            if (_newest == operation)
            {
                _newest = null;
            }

            _operations--;
        }

        operation.Dispose();
    }

    // Between operations the status follows the engine, which the manager restarts after an unexpected exit.
    private void OnEngineStateChanged(object? sender, ModelProcessStateChangedEventArgs change)
    {
        lock (_sync)
        {
            if (_operations > 0 || _model is null)
            {
                return;
            }

            var modelId = _report.ModelId;
            switch (change.State)
            {
                case ModelProcessState.Restarting:
                    SetStatus(ModelStatus.Loading, modelId, null);
                    break;
                case ModelProcessState.Running:
                    SetStatus(ModelStatus.Ready, modelId, null);
                    break;
                case ModelProcessState.Failed:
                    _model = null;
                    SetStatus(
                        ModelStatus.Failed,
                        modelId,
                        FailureOf(change.Failure ?? ModelProcessFailure.RestartLimitReached));
                    break;
            }
        }
    }

    // Must be called with _sync held.
    private void SetStatus(ModelStatus status, string? modelId, ModelFailure? failure)
    {
        if (_report.Status == status && _report.ModelId == modelId && _report.Failure == failure)
        {
            return;
        }

        _report = new ModelStatusReport(_report.Sequence + 1, status) { ModelId = modelId, Failure = failure };
        if (failure is { } reason)
        {
            ModelControllerLog.StatusFailed(_logger, status, reason, _report.Sequence);
        }
        else
        {
            ModelControllerLog.StatusChanged(_logger, status, _report.Sequence);
        }

        Changed?.Invoke(this, _report);
    }

    private static ModelFailure FailureOf(ModelProcessFailure failure) => failure switch
    {
        ModelProcessFailure.RuntimeUnavailable or ModelProcessFailure.NativeLoadFailed => ModelFailure.RuntimeUnavailable,
        ModelProcessFailure.RestartLimitReached => ModelFailure.EngineStopped,
        _ => ModelFailure.LoadFailed,
    };

    private static ModelHostErrorCode CodeOf(ModelProcessFailure failure) => FailureOf(failure) switch
    {
        ModelFailure.RuntimeUnavailable => ModelHostErrorCode.RuntimeUnavailable,
        _ => ModelHostErrorCode.ModelLoadFailed,
    };
}
