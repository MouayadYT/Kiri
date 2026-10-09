using Assistant.Core.Contracts;

namespace Assistant.Core.Events;

/// <summary>The local model's availability changed.</summary>
/// <param name="Status">The new status.</param>
public sealed record ModelStatusChanged(ModelStatus Status)
{
    /// <summary>The model concerned, or <see langword="null"/> when none is installed.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Load progress from 0 to 1 while <see cref="Status"/> is <see cref="ModelStatus.Loading"/>; otherwise
    /// <see langword="null"/>, also while the progress is not known.
    /// </summary>
    public double? LoadProgress { get; init; }

    /// <summary>Why the model failed, while <see cref="Status"/> is <see cref="ModelStatus.Failed"/>; otherwise <see langword="null"/>.</summary>
    public ModelFailure? Failure { get; init; }
}
