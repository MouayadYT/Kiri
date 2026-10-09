using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Processes;

/// <summary>The model engine could not be started. Its message is fixed text for the failure, without paths.</summary>
internal sealed class ModelProcessException(ModelProcessFailure failure) : Exception(Describe(failure))
{
    /// <summary>Why the engine could not be started.</summary>
    public ModelProcessFailure Failure { get; } = failure;

    /// <summary>The runtime's state, for <see cref="ModelProcessFailure.RuntimeUnavailable"/>.</summary>
    public ModelRuntimeState? RuntimeState { get; init; }

    private static string Describe(ModelProcessFailure failure) => failure switch
    {
        ModelProcessFailure.RuntimeUnavailable => "The model runtime is not available.",
        ModelProcessFailure.NativeLoadFailed => "Windows could not load the model runtime's native files.",
        ModelProcessFailure.EndpointUnavailable => "The model engine's socket could not be created.",
        ModelProcessFailure.ModelLoadFailed => "The model engine could not load the model.",
        ModelProcessFailure.ExitedDuringStart => "The model engine exited while it was starting.",
        ModelProcessFailure.StartTimedOut => "The model engine did not become ready in time.",
        ModelProcessFailure.RestartLimitReached => "The model engine kept exiting and was not restarted again.",
        _ => "The model engine could not be started.",
    };
}
