using System.Text;

namespace Assistant.Core.ImageSearch;

/// <summary>One match an image search found: a picture on a page of the web, with what the page calls it and which site it is on.</summary>
/// <param name="Title">What the page calls it, such as the title of a video or an article.</param>
/// <param name="SourceName">The site it is on, such as <c>YouTube</c>, shown beside the title.</param>
/// <param name="PageUrl">The page the picture is on, which a click opens in the user's browser; <see langword="null"/> for a sample result, which has none.</param>
/// <param name="Thumbnail">A small picture of the match, encoded as PNG or JPEG; empty when the provider gave none.</param>
public sealed record ImageSearchResult(string Title, string SourceName, Uri? PageUrl, ReadOnlyMemory<byte> Thumbnail)
{
    /// <summary>The thumbnail's width and height, in pixels, when the provider says (the cards are laid out by their shape).</summary>
    public (int Width, int Height)? ThumbnailSize { get; init; }

    // Keeps what the web returned out of ToString, and so out of logs (PROJECT_SPEC §3.2).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"HasUrl = {PageUrl is not null}, ThumbnailBytes = {Thumbnail.Length}");
        return true;
    }
}

/// <summary>What an image search found, and who found it.</summary>
/// <param name="ProviderName">The search provider's name, which the results are headed with ("Results from Google").</param>
/// <param name="IsSample">Whether these are made-up sample results, because no search provider is set up: nothing was sent anywhere.</param>
/// <param name="Results">The matches, best first.</param>
public sealed record ImageSearchResults(string ProviderName, bool IsSample, IReadOnlyList<ImageSearchResult> Results)
{
    /// <summary>
    /// Where the user can report a concern about these results, as the provider's page for it, which a click opens in the browser;
    /// <see langword="null"/> when the provider has none (a sample has none).
    /// </summary>
    public Uri? ReportUrl { get; init; }

    // Keeps what the web returned out of ToString, and so out of logs (PROJECT_SPEC §3.2).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsSample = {IsSample}, Results = {Results.Count}");
        return true;
    }
}

/// <summary>Why an image search cannot be done now, or <see cref="None"/> when it can.</summary>
public enum ImageSearchBlock
{
    /// <summary>Nothing stops it.</summary>
    None = 0,

    /// <summary>Local Only mode is on: nothing leaves this PC.</summary>
    LocalOnly = 1,

    /// <summary>The user has turned off External Web and Image Search in the Permissions.</summary>
    PermissionOff = 2,

    /// <summary>There is no search provider at all, not even a sample one.</summary>
    NoProvider = 3,
}

/// <summary>Whether an image search can be done now, and with whom.</summary>
/// <param name="Block">Why not, or <see cref="ImageSearchBlock.None"/>.</param>
/// <param name="ProviderName">The provider that would search, or an empty string with none.</param>
/// <param name="IsSample">Whether that provider only makes up sample results and sends nothing anywhere.</param>
public sealed record ImageSearchAvailability(ImageSearchBlock Block, string ProviderName, bool IsSample)
{
    /// <summary>Whether a search can be done.</summary>
    public bool IsAvailable => Block == ImageSearchBlock.None;
}

/// <summary>How an image search ended.</summary>
public enum ImageSearchOutcomeKind
{
    /// <summary>It found something (or sample results), in <see cref="ImageSearchOutcome.Results"/>.</summary>
    Results = 0,

    /// <summary>The user was asked whether to send the picture and said no, so nothing was sent.</summary>
    Declined = 1,

    /// <summary>It could not be done: see <see cref="ImageSearchOutcome.Block"/>. Nothing was sent.</summary>
    Blocked = 2,

    /// <summary>The provider could not search (no connection, an error): there are no results to show.</summary>
    Failed = 3,
}

/// <summary>The end of an image search: its results, or why there are none.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Results">The results of a <see cref="ImageSearchOutcomeKind.Results"/> outcome, otherwise <see langword="null"/>.</param>
/// <param name="Block">Why a <see cref="ImageSearchOutcomeKind.Blocked"/> outcome was blocked, otherwise <see cref="ImageSearchBlock.None"/>.</param>
public sealed record ImageSearchOutcome(ImageSearchOutcomeKind Kind, ImageSearchResults? Results = null, ImageSearchBlock Block = ImageSearchBlock.None);

/// <summary>What the user is told before a picture is sent off this PC, for them to decide on.</summary>
/// <param name="ProviderName">Who it would be sent to.</param>
/// <param name="Image">The picture that would be sent, for the user to see, as it is sent.</param>
public sealed record ImageSearchDisclosure(string ProviderName, ReadOnlyMemory<byte> Image)
{
    // The picture is private content (PROJECT_SPEC §3.2).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ImageBytes = {Image.Length}");
        return true;
    }
}
