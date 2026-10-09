namespace Assistant.ModelHost.Server;

/// <summary>Why the model host stopped serving, and so exits.</summary>
internal enum ModelHostExitReason
{
    /// <summary>The host itself was stopped, for example as Windows shut down.</summary>
    Stopped = 0,

    /// <summary>The owner sent a shutdown request.</summary>
    ShutdownRequested = 1,

    /// <summary>The owner closed the connection.</summary>
    OwnerDisconnected = 2,

    /// <summary>The owner process exited.</summary>
    OwnerExited = 3,

    /// <summary>The owner did not connect in time.</summary>
    ConnectTimedOut = 4,

    /// <summary>The owner broke the framing, so the connection could not continue.</summary>
    ProtocolViolation = 5,

    /// <summary>The pipe could not be created, for example because another process already holds its name.</summary>
    PipeUnavailable = 6,
}
