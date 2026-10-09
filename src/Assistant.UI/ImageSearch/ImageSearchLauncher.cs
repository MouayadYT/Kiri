using Assistant.Core.ImageSearch;
using Assistant.UI.Capture;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Assistant.Core.Contracts;

namespace Assistant.UI.ImageSearch;

/// <summary>
/// What the Image Search chip does with a part of the screen (PROJECT_SPEC §4.6): hands its picture to the image search flow, which
/// decides whether it may be searched with and, when it would leave this PC, asks the user first, and shows what was found in the results
/// window, or says in the floating conversation why there is nothing to show. Only the selected part is ever given to it, and the picture
/// is encoded in memory for the search and not kept.
/// </summary>
internal sealed class ImageSearchLauncher(
    ImageSearchFlow flow,
    IImageSearchResultsWindow results,
    IUrlLauncher urls,
    ConversationViewModel conversation,
    AssistantWindowStateController windows)
{
    /// <summary>What the Image Search chip should say now, for the settings as they are saved.</summary>
    public async Task<ChipAvailability> GetChipAvailabilityAsync(CancellationToken cancellationToken) =>
        ImageSearchChipText.For(await flow.GetAvailabilityAsync(cancellationToken).ConfigureAwait(true));

    /// <summary>Searches with <paramref name="region"/>, if the rules allow and the user agrees, and shows the outcome.</summary>
    public async Task SearchAsync(CapturedImage region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var picture = await CapturedImageEncoder.EncodePngAsync(region).ConfigureAwait(true);
        var outcome = await flow.SearchAsync(picture).ConfigureAwait(true);
        switch (outcome.Kind)
        {
            case ImageSearchOutcomeKind.Results when outcome.Results is { } found:
                results.ShowResults(new ImageSearchResultsViewModel(found, urls));
                break;
            case ImageSearchOutcomeKind.Blocked:
                Tell(ImageSearchChipText.Blocked(outcome.Block));
                break;
            case ImageSearchOutcomeKind.Failed:
                Tell(ImageSearchChipText.Failed);
                break;
        }

        // Declined: the user said no, and nothing more is said.
    }

    // Says why there is nothing to show, in the floating conversation.
    private void Tell(string text)
    {
        conversation.StartWithNotice(text);
        windows.ShowConversation();
    }
}
