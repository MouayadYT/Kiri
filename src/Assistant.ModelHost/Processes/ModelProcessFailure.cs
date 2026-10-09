namespace Assistant.ModelHost.Processes;

/// <summary>Why the model engine could not be started or kept running.</summary>
internal enum ModelProcessFailure
{
    /// <summary>The bundled runtime cannot run, or Windows refused to launch it.</summary>
    RuntimeUnavailable = 0,

    /// <summary>Windows could not load the engine's native files, so it exited as it started.</summary>
    NativeLoadFailed = 1,

    /// <summary>The engine's socket could not be created.</summary>
    EndpointUnavailable = 2,

    /// <summary>The engine could not load the model, for example because the file is damaged or memory ran out.</summary>
    ModelLoadFailed = 3,

    /// <summary>The engine exited before it was ready, without saying why.</summary>
    ExitedDuringStart = 4,

    /// <summary>The engine was not ready within <see cref="ModelProcessOptions.StartTimeout"/>.</summary>
    StartTimedOut = 5,

    /// <summary>The engine kept exiting, and the restart policy's limit was reached.</summary>
    RestartLimitReached = 6,
}
