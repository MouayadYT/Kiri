namespace Assistant.ModelHost.Processes;

/// <summary>
/// Owns the model engine, the llama.cpp server bundled with the app, as a hidden child process of the host: starts it,
/// restarts it when it exits unexpectedly (within a bounded policy), and ends it when asked or when the host shuts
/// down. One engine runs at a time. The engine loads its model as it starts and returns the model's memory as it
/// exits, so <see cref="StartAsync"/> is the command that loads a model and <see cref="StopAsync"/> the one that
/// unloads it; <see cref="ModelProcessState"/> is the status of both.
/// </summary>
internal interface IModelProcessManager
{
    /// <summary>Where the engine is in its life.</summary>
    ModelProcessState State { get; }

    /// <summary>Why the engine failed, while <see cref="State"/> is <see cref="ModelProcessState.Failed"/>.</summary>
    ModelProcessFailure? Failure { get; }

    /// <summary>The running engine's process id, or <see langword="null"/> when none runs.</summary>
    int? ProcessId { get; }

    /// <summary>
    /// The UNIX socket the running engine listens on, or <see langword="null"/> when none runs. A restart picks a new one.
    /// </summary>
    string? SocketPath { get; }

    /// <summary>
    /// The context, in tokens, that the running engine reported for the model, or <see langword="null"/> when none
    /// runs or it has not said.
    /// </summary>
    int? ContextLength { get; }

    /// <summary>Raised whenever <see cref="State"/> changes, on the thread that changed it.</summary>
    event EventHandler<ModelProcessStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Starts the engine for <paramref name="launch"/>, ending any engine already running, and waits until it has loaded
    /// the model and listens. A failure here is not retried: the restart policy covers an engine that was running.
    /// </summary>
    /// <exception cref="ModelProcessException">The engine could not be started; the manager is then failed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, or <see cref="StopAsync"/> was called, before the engine was
    /// ready. The engine has been ended.
    /// </exception>
    Task StartAsync(ModelProcessLaunch launch, CancellationToken cancellationToken = default);

    /// <summary>Ends the engine, and any restart or start in progress, and waits until its process has exited.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
