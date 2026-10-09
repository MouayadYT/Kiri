using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Tells the app that the model's status changed. The host sends it on its own, whenever the status changes, with
/// request id 0: a load, an unload, or an engine that exits and starts again all show up here. The status is what the
/// app shows (<see cref="Events.ModelStatusChanged"/>).
/// </summary>
/// <param name="Sequence">
/// Counts the host's status changes from 1, so a reader can tell which of two reports is newer whatever order they
/// arrive in.
/// </param>
/// <param name="Status">The new status.</param>
public sealed record ModelStatusReport(long Sequence, ModelStatus Status) : ModelHostReply
{
    /// <summary>The model concerned, or <see langword="null"/> when none is loaded or loading.</summary>
    public string? ModelId { get; init; }

    /// <summary>Why the model failed, while <see cref="Status"/> is <see cref="ModelStatus.Failed"/>.</summary>
    public ModelFailure? Failure { get; init; }

    internal override bool IsWellFormed() => Sequence >= 1 && Enum.IsDefined(Status);
}
