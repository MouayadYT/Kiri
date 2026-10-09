using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Processes;

/// <summary>
/// Runs the bundled llama.cpp server as the host's hidden child process (<see cref="IModelProcessManager"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each launch gets a new socket name in <see cref="ModelProcessOptions.SocketDirectory"/>, is checked against the
/// runtime locator first, and counts as started once the server says it listens. The server's output is read only
/// through <see cref="LlamaServerOutput"/>, so the log holds process ids, states, counts and timings, never its text.
/// </para>
/// <para>
/// After an unexpected exit the engine is started again as <see cref="ModelProcessOptions.RestartPolicy"/> allows, and
/// left <see cref="ModelProcessState.Failed"/> once the policy gives up, or at once when Windows could not load its
/// DLLs. Every engine process is put in a <see cref="ProcessJob"/>, so it also ends if the host crashes.
/// </para>
/// </remarks>
internal sealed class ModelProcessManager : IModelProcessManager, IAsyncDisposable, IDisposable
{
    // Exit codes with which Windows ends a process whose DLLs it could not load: starting it again cannot help.
    private static readonly int[] NativeLoadFailureCodes =
    [
        unchecked((int)0xC0000135), // STATUS_DLL_NOT_FOUND
        unchecked((int)0xC0000138), // STATUS_ORDINAL_NOT_FOUND
        unchecked((int)0xC0000139), // STATUS_ENTRYPOINT_NOT_FOUND
        unchecked((int)0xC0000142), // STATUS_DLL_INIT_FAILED
        unchecked((int)0xC000007B), // STATUS_INVALID_IMAGE_FORMAT
    ];

    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    private readonly IModelRuntimeLocator _locator;
    private readonly ModelProcessOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelProcessManager> _logger;
    private readonly IEngineDeviceProbe? _deviceProbe;
    private readonly ProcessJob? _job;

    // Starts and stops take turns; _sync guards the fields that the supervision and the properties also read.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    // One launch, from its start to its stop: cancelling it stops a start in progress and the supervision alike. It has
    // no timer, so it is left to the collector rather than disposed while a StopAsync may still be cancelling it.
    private CancellationTokenSource? _session;
    private Task _supervision = Task.CompletedTask;
    private EngineProcess? _engine;
    private ModelProcessState _state;
    private ModelProcessFailure? _failure;
    private int _disposed;

    public ModelProcessManager(
        IModelRuntimeLocator locator,
        ModelProcessOptions options,
        TimeProvider timeProvider,
        ILogger<ModelProcessManager> logger,
        IEngineDeviceProbe? deviceProbe = null)
    {
        _locator = locator;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _deviceProbe = deviceProbe;
        try
        {
            _job = ProcessJob.Create();
        }
        catch (Win32Exception exception)
        {
            // Engines are still ended when the host stops; only a crash of the host could leave one running.
            ModelProcessLog.JobUnavailable(logger, exception);
        }
    }

    public event EventHandler<ModelProcessStateChangedEventArgs>? StateChanged;

    public ModelProcessState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public ModelProcessFailure? Failure
    {
        get
        {
            lock (_sync)
            {
                return _failure;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _engine?.ProcessId;
            }
        }
    }

    public string? SocketPath
    {
        get
        {
            lock (_sync)
            {
                return _engine?.SocketPath;
            }
        }
    }

    public int? ContextLength
    {
        get
        {
            lock (_sync)
            {
                return _engine?.ContextTokens is > 0 and var tokens ? tokens : null;
            }
        }
    }

    public async Task StartAsync(ModelProcessLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!Path.IsPathFullyQualified(launch.ModelPath))
        {
            throw new ArgumentException("The model's path must be fully qualified.", nameof(launch));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await EndSessionAsync().ConfigureAwait(false);

            var session = new CancellationTokenSource();
            lock (_sync)
            {
                _session = session;
            }

            SetState(ModelProcessState.Starting, null);
            EngineProcess engine;
            try
            {
                using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
                launch = await ChooseDevicesAsync(launch, starting.Token).ConfigureAwait(false);
                try
                {
                    engine = await LaunchAsync(launch, starting.Token).ConfigureAwait(false);
                }
                catch (ModelProcessException failed) when (launch.Devices is { Count: > 0 }
                    && failed.Failure is ModelProcessFailure.ModelLoadFailed or ModelProcessFailure.ExitedDuringStart)
                {
                    // A driver can fail during its first initialization. Retry once on the same devices;
                    // silently pinning this session to CPU would ignore the user's GPU choice until unload.
                    EngineDeviceLog.RetryingGpu(_logger, failed.Failure);
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, starting.Token).ConfigureAwait(false);
                    engine = await LaunchAsync(launch, starting.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await EndSessionAsync().ConfigureAwait(false);
                if (exception is ModelProcessException failed)
                {
                    SetState(ModelProcessState.Failed, failed.Failure);
                }
                else
                {
                    SetState(ModelProcessState.Stopped, null);
                }

                throw;
            }

            SetEngine(engine);
            SetState(ModelProcessState.Running, null);
            _supervision = SuperviseAsync(launch, engine, session.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // First, so a start that is waiting for its engine gives the turn up at once.
        CancelSession();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Unloading takes a moment: the engine has to exit for its memory to come back.
            if (State is ModelProcessState.Starting or ModelProcessState.Running or ModelProcessState.Restarting)
            {
                SetState(ModelProcessState.Stopping, null);
            }

            await EndSessionAsync().ConfigureAwait(false);
            SetState(ModelProcessState.Stopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the engine, then closes the job, which ends anything still in it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _job?.Dispose();
    }

    /// <summary>
    /// Stops the engine without waiting for a start in progress, then closes the job, which ends anything still in it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelSession();
        Volatile.Read(ref _supervision).Wait(DisposeWait);
        _job?.Dispose();
    }

    // Names the devices the model runs on when the launch does not: the engine's devices are listed, and the fewest that
    // hold the model are chosen. Without a probe, or when no device is found, the engine decides.
    private async Task<ModelProcessLaunch> ChooseDevicesAsync(ModelProcessLaunch launch, CancellationToken cancellationToken)
    {
        if (launch.Devices is not null || _deviceProbe is null || _locator.Locate() is not { IsReady: true, Runtime: { } runtime })
        {
            return launch;
        }

        var devices = await _deviceProbe.ListAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            // On a fresh install the graphics backend can still be initializing. Do not make the
            // session's automatic choice from a single empty/failed probe.
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
            devices = await _deviceProbe.ListAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        var required = EngineDevices.RequiredBytes(SizeOf(launch.ModelPath), SizeOf(launch.ProjectorPath));
        if (EngineDevices.Choose(devices, required) is not { } chosen)
        {
            return launch;
        }

        EngineDeviceLog.Chosen(_logger, chosen.Count, devices.Count);
        return launch with { Devices = chosen };
    }

    private static long SizeOf(string? path)
    {
        try
        {
            return path is null ? 0 : new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<EngineProcess> LaunchAsync(ModelProcessLaunch launch, CancellationToken cancellationToken)
    {
        var status = _locator.Locate();
        if (!status.IsReady)
        {
            ModelProcessLog.StartFailed(_logger, ModelProcessFailure.RuntimeUnavailable, null);
            throw new ModelProcessException(ModelProcessFailure.RuntimeUnavailable) { RuntimeState = status.State };
        }

        var socketPath = CreateSocketPath();
        var start = _timeProvider.GetTimestamp();
        EngineProcess engine;
        try
        {
            engine = EngineProcess.Start(
                LlamaServerCommand.Create(status.Runtime, launch, socketPath), socketPath, _timeProvider, OnSignal);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            ModelProcessLog.LaunchFailed(_logger, exception);
            throw new ModelProcessException(ModelProcessFailure.RuntimeUnavailable) { RuntimeState = status.State };
        }

        ModelProcessLog.Launched(_logger, engine.ProcessId);
        AssignToJob(engine);
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = Task.Delay(_options.StartTimeout, _timeProvider, waiting.Token);
            var first = await Task.WhenAny(engine.Ready, engine.Exited, timeout).ConfigureAwait(false);
            waiting.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            if (first == engine.Ready)
            {
                ModelProcessLog.Ready(_logger, engine.ProcessId, Milliseconds(_timeProvider.GetElapsedTime(start)));
                return engine;
            }

            int? exitCode = null;
            var failure = ModelProcessFailure.StartTimedOut;
            if (first == engine.Exited)
            {
                exitCode = await engine.Exited.ConfigureAwait(false);
                await engine.WaitForOutputAsync().ConfigureAwait(false);
                failure = FailureOf(exitCode.Value, engine.ReportedFailure);
            }

            ModelProcessLog.StartFailed(_logger, failure, exitCode);
            throw new ModelProcessException(failure);
        }
        catch
        {
            await engine.EndAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Watches a running engine and starts it again after an unexpected exit, until the session is cancelled or the
    // policy gives up. It ends whichever engine it holds as it stops, and never throws.
    private async Task SuperviseAsync(ModelProcessLaunch launch, EngineProcess engine, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var policy = _options.RestartPolicy;
        var restarts = 0;
        try
        {
            while (true)
            {
                var exitCode = await engine.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                await engine.EndAsync().ConfigureAwait(false);
                ClearEngine(engine);
                ModelProcessLog.ExitedUnexpectedly(
                    _logger,
                    engine.ProcessId,
                    exitCode,
                    Milliseconds(engine.Uptime),
                    engine.ErrorCount,
                    engine.WarningCount);

                if (NativeLoadFailureCodes.Contains(exitCode))
                {
                    GiveUp(ModelProcessFailure.NativeLoadFailed);
                    return;
                }

                if (engine.Uptime >= policy.StableUptime)
                {
                    restarts = 0;
                }

                EngineProcess? next = null;
                while (next is null)
                {
                    if (policy.DelayBefore(++restarts) is not { } delay)
                    {
                        GiveUp(ModelProcessFailure.RestartLimitReached);
                        return;
                    }

                    SetState(ModelProcessState.Restarting, null);
                    ModelProcessLog.Restarting(_logger, Milliseconds(delay), restarts, policy.MaxRestarts);
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        next = await LaunchAsync(launch, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ModelProcessException exception) when (exception.Failure is
                        ModelProcessFailure.RuntimeUnavailable or ModelProcessFailure.NativeLoadFailed)
                    {
                        GiveUp(exception.Failure);
                        return;
                    }
                    catch (ModelProcessException)
                    {
                        // A failed restart counts toward the limit, and the next one waits longer.
                    }
                }

                engine = next;
                SetEngine(engine);
                SetState(ModelProcessState.Running, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped, or replaced by a new start.
        }
        finally
        {
            var wasRunning = !engine.Exited.IsCompleted;
            await engine.EndAsync().ConfigureAwait(false);
            ClearEngine(engine);
            if (wasRunning)
            {
                ModelProcessLog.Stopped(_logger, engine.ProcessId, Milliseconds(engine.Uptime));
            }
        }
    }

    // Ends the current launch, if any: its start in progress, its supervision and its engine.
    private async Task EndSessionAsync()
    {
        CancellationTokenSource? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
        {
            return;
        }

        session.Cancel();
        try
        {
            await _supervision.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ModelProcessLog.StopFailed(_logger, exception);
        }

        _supervision = Task.CompletedTask;
    }

    private void CancelSession()
    {
        CancellationTokenSource? session;
        lock (_sync)
        {
            session = _session;
        }

        session?.Cancel();
    }

    private string CreateSocketPath()
    {
        var path = Path.Combine(
            _options.SocketDirectory, $"engine-{RandomNumberGenerator.GetHexString(8, lowercase: true)}.sock");
        var byteCount = Encoding.UTF8.GetByteCount(path);
        if (byteCount > ModelProcessOptions.MaxSocketPathBytes)
        {
            ModelProcessLog.SocketPathTooLong(_logger, byteCount, ModelProcessOptions.MaxSocketPathBytes);
            throw new ModelProcessException(ModelProcessFailure.EndpointUnavailable);
        }

        try
        {
            Directory.CreateDirectory(_options.SocketDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ModelProcessLog.SocketFolderUnavailable(_logger, exception);
            throw new ModelProcessException(ModelProcessFailure.EndpointUnavailable);
        }

        return path;
    }

    private void AssignToJob(EngineProcess engine)
    {
        if (_job is null)
        {
            return;
        }

        try
        {
            engine.AssignTo(_job);
        }
        catch (Win32Exception exception)
        {
            ModelProcessLog.JobUnavailable(_logger, exception);
        }
    }

    private void OnSignal(EngineProcess engine, LlamaServerSignal signal)
    {
        var id = engine.ProcessId;
        switch (signal.Kind)
        {
            case LlamaServerSignalKind.ModelLoaded:
                ModelProcessLog.ModelLoaded(_logger, id);
                break;
            case LlamaServerSignalKind.ModelLoadFailed:
                ModelProcessLog.ModelLoadFailed(_logger, id);
                break;
            case LlamaServerSignalKind.EndpointFailed:
                ModelProcessLog.EndpointFailed(_logger, id);
                break;
            case LlamaServerSignalKind.OutOfMemory:
                ModelProcessLog.OutOfMemory(_logger, id);
                break;
            case LlamaServerSignalKind.Threads:
                ModelProcessLog.Threads(_logger, id, signal.Count);
                break;
            case LlamaServerSignalKind.Slots:
                ModelProcessLog.Slots(_logger, id, signal.Count, signal.ContextTokens);
                break;
            case LlamaServerSignalKind.PromptEvaluated:
                ModelProcessLog.PromptEvaluated(_logger, id, signal.Count, signal.ElapsedMs);
                break;
            case LlamaServerSignalKind.Generated:
                ModelProcessLog.Generated(_logger, id, signal.Count, signal.ElapsedMs);
                break;
            case LlamaServerSignalKind.Warning:
                ModelProcessLog.ReportedWarning(_logger, id);
                break;
            case LlamaServerSignalKind.Error:
                ModelProcessLog.ReportedError(_logger, id);
                break;
        }
    }

    private void GiveUp(ModelProcessFailure failure)
    {
        ModelProcessLog.GaveUp(_logger, failure);
        SetState(ModelProcessState.Failed, failure);
    }

    private void SetState(ModelProcessState state, ModelProcessFailure? failure)
    {
        lock (_sync)
        {
            if (_state == state && _failure == failure)
            {
                return;
            }

            _state = state;
            _failure = failure;
        }

        StateChanged?.Invoke(this, new ModelProcessStateChangedEventArgs(state, failure));
    }

    private void SetEngine(EngineProcess engine)
    {
        lock (_sync)
        {
            _engine = engine;
        }
    }

    private void ClearEngine(EngineProcess engine)
    {
        lock (_sync)
        {
            if (_engine == engine)
            {
                _engine = null;
            }
        }
    }

    private static ModelProcessFailure FailureOf(int exitCode, LlamaServerSignalKind? reported) =>
        NativeLoadFailureCodes.Contains(exitCode) ? ModelProcessFailure.NativeLoadFailed
        : reported == LlamaServerSignalKind.ModelLoadFailed ? ModelProcessFailure.ModelLoadFailed
        : reported == LlamaServerSignalKind.EndpointFailed ? ModelProcessFailure.EndpointUnavailable
        : ModelProcessFailure.ExitedDuringStart;

    private static long Milliseconds(TimeSpan duration) => (long)duration.TotalMilliseconds;
}
