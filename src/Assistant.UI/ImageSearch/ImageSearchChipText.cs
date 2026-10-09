using Assistant.Core.ImageSearch;
using Assistant.UI.Capture;

namespace Assistant.UI.ImageSearch;

/// <summary>
/// What the Image Search chip says about itself (PROJECT_SPEC §4.6): enabled when a search can be done, and when it cannot, why not, in
/// words that tell the user what to turn on. It is marked as the one that goes online, and a sample, which sends nothing, says so.
/// </summary>
internal static class ImageSearchChipText
{
    /// <summary>What it says while Local Only mode is on.</summary>
    internal const string LocalOnly =
        "Image Search sends the picture to a search provider on the web, so it is off while Local Only mode is on. Turn Local Only off in Settings, under Privacy.";

    /// <summary>What it says while External Web and Image Search is turned off in the Permissions.</summary>
    internal const string PermissionOff =
        "Image Search is turned off in Settings, under Permissions (External Web and Image Search).";

    /// <summary>What it says when there is no search provider at all.</summary>
    internal const string NoProvider = "Image Search has no search provider set up.";

    /// <summary>What it says for a sample provider, which sends nothing anywhere.</summary>
    internal const string Sample =
        "No search provider is set up yet, so this shows sample results. Nothing is sent anywhere.";

    /// <summary>The chip's state for <paramref name="availability"/>.</summary>
    public static ChipAvailability For(ImageSearchAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return availability.Block switch
        {
            ImageSearchBlock.LocalOnly => new ChipAvailability(false, LocalOnly),
            ImageSearchBlock.PermissionOff => new ChipAvailability(false, PermissionOff),
            ImageSearchBlock.NoProvider => new ChipAvailability(false, NoProvider),
            _ when availability.IsSample => new ChipAvailability(true, Sample),
            _ => new ChipAvailability(
                true,
                $"Searches the web with this picture. It is sent to {availability.ProviderName}, and only after you confirm it."),
        };
    }

    /// <summary>What the user is told when a search was blocked after the chip was pressed (a setting changed in between).</summary>
    public static string Blocked(ImageSearchBlock block) => block switch
    {
        ImageSearchBlock.LocalOnly => "Image search is off while Local Only mode is on, so the picture was not sent. " +
            "Turn Local Only off in Settings, under Privacy, to search with a picture.",
        ImageSearchBlock.PermissionOff => "External Web and Image Search is turned off in Settings, under Permissions, so the picture was not sent.",
        _ => "There is no image search provider set up, so nothing was sent.",
    };

    /// <summary>What the user is told when the provider could not search.</summary>
    internal const string Failed = "The image search didn't work, so there are no results. Check your connection and try again.";
}
