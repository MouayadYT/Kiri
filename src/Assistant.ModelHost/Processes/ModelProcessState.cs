namespace Assistant.ModelHost.Processes;

/// <summary>Where the model engine process is in its life (<see cref="IModelProcessManager"/>).</summary>
internal enum ModelProcessState
{
    /// <summary>No engine runs: it was never started, or it was stopped.</summary>
    Stopped = 0,

    /// <summary>The engine was launched and is loading its model.</summary>
    Starting = 1,

    /// <summary>The engine has loaded its model and listens on its socket.</summary>
    Running = 2,

    /// <summary>The engine exited unexpectedly and is being started again.</summary>
    Restarting = 3,

    /// <summary>The engine could not be started, or kept exiting; <see cref="IModelProcessManager.Failure"/> says why.</summary>
    Failed = 4,

    /// <summary>The engine is being ended, which unloads the model and frees its memory.</summary>
    Stopping = 5,
}
