namespace Assistant.ModelHost.Processes;

/// <summary>The model engine moved to <see cref="State"/>.</summary>
internal sealed class ModelProcessStateChangedEventArgs(ModelProcessState state, ModelProcessFailure? failure) : EventArgs
{
    /// <summary>The new state.</summary>
    public ModelProcessState State { get; } = state;

    /// <summary>Why the engine failed, when <see cref="State"/> is <see cref="ModelProcessState.Failed"/>.</summary>
    public ModelProcessFailure? Failure { get; } = failure;
}
