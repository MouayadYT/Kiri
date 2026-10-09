using Assistant.Windows.Capture;

namespace Assistant.UI.Capture;

/// <summary>What the user chose to do with the part of the screen they selected.</summary>
internal enum CaptureAction
{
    /// <summary>Ask the Assistant about it: it opens the conversation with the picture attached.</summary>
    Ask,

    /// <summary>Copy the picture to the clipboard.</summary>
    Copy,

    /// <summary>Search the web with the picture, which sends it off this PC.</summary>
    ImageSearch,
}

/// <summary>
/// The part of the screen that was selected, and what the user chose to do with it (PROJECT_SPEC §4.6). The picture is memory the
/// receiver owns, and disposes when it is done with it, which wipes it.
/// </summary>
internal sealed class CaptureOutcome : IDisposable
{
    /// <summary>Creates an outcome.</summary>
    /// <param name="action">What the user chose.</param>
    /// <param name="region">The selected part of the snapshot, as a picture of its own.</param>
    /// <param name="question">What the user typed in the Ask chip, which is empty when nothing was.</param>
    public CaptureOutcome(CaptureAction action, CapturedImage region, string question)
    {
        ArgumentNullException.ThrowIfNull(region);
        Action = action;
        Region = region;
        Question = question ?? string.Empty;
    }

    /// <summary>What the user chose to do.</summary>
    public CaptureAction Action { get; }

    /// <summary>The selected part of the screen, as it was when the capture started.</summary>
    public CapturedImage Region { get; }

    /// <summary>What was typed in the Ask chip, trimmed, or an empty string.</summary>
    public string Question { get; }

    /// <summary>Wipes the picture.</summary>
    public void Dispose() => Region.Dispose();

    // The picture and the question are private content (PROJECT_SPEC §3.2): ToString, and so any log, holds neither.
    /// <inheritdoc/>
    public override string ToString() => $"CaptureOutcome {{ Action = {Action}, QuestionLength = {Question.Length} }}";
}

/// <summary>Shows the overlay for choosing a part of the screen, and says what was chosen.</summary>
internal interface ICaptureOverlay
{
    /// <summary>
    /// Shows <paramref name="snapshots"/>, one picture of each monitor, dimmed and frozen, each over the monitor it is of, until the user
    /// selects a part of one and chooses what to do with it, or gives up (Esc, or the right button). It takes the snapshots: they are
    /// wiped when it is done, whatever the outcome.
    /// </summary>
    /// <param name="snapshots">The monitors, as they were a moment ago.</param>
    /// <param name="imageSearch">Whether Image Search can be used, and why not if it cannot.</param>
    /// <param name="cancellationToken">Takes the overlay down without an outcome.</param>
    /// <returns>What the user chose, or <see langword="null"/> when they gave up.</returns>
    Task<CaptureOutcome?> SelectAsync(
        IReadOnlyList<CapturedImage> snapshots, ChipAvailability imageSearch, CancellationToken cancellationToken);
}
