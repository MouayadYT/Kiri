using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// What to tell the user about the local model's status: one plain sentence that says where it stands and, for a
/// failure, what to do about it. It never holds a path, so it is also safe in diagnostics (PROJECT_SPEC §3.3).
/// </summary>
public static class ModelStatusText
{
    /// <summary>Describes <paramref name="status"/>, and <paramref name="failure"/> when the status is <see cref="ModelStatus.Failed"/>.</summary>
    public static string Describe(ModelStatus status, ModelFailure? failure = null) => status switch
    {
        ModelStatus.NotInstalled => "No local model is set up yet.",
        ModelStatus.NotLoaded => "The local model isn't loaded.",
        ModelStatus.Loading => "Loading the local model…",
        ModelStatus.Ready => "The local model is ready.",
        ModelStatus.Unloading => "Unloading the local model…",
        ModelStatus.Failed => DescribeFailure(failure),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string DescribeFailure(ModelFailure? failure) => failure switch
    {
        ModelFailure.ModelNotFound => "A model file couldn't be found. Check the file paths.",
        ModelFailure.RuntimeUnavailable =>
            "The local model runtime can't run. Reinstall the Assistant to restore it.",
        ModelFailure.LoadFailed =>
            "The model couldn't be loaded. A file may be damaged or not a model, or the PC may be out of memory.",
        ModelFailure.EngineStopped => "The local model stopped unexpectedly and wasn't started again.",
        ModelFailure.HostUnavailable => "The local model process couldn't be started.",
        _ => "The local model couldn't be loaded.",
    };
}
