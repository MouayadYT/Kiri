using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Contracts;
using Assistant.Core.ImageSearch;
using Assistant.UI.ViewModels;

namespace Assistant.UI.ImageSearch;

/// <summary>
/// One card of the image search results (PROJECT_SPEC §4.6): a thumbnail shaped like the picture, what the page calls it, and the site it
/// is on with a small mark for the site. A sample result has no page, so it opens nothing.
/// </summary>
public sealed class ImageSearchCardViewModel
{
    /// <summary>The widest a thumbnail is decoded, in pixels: a card is 178 DIPs wide, at up to 2x.</summary>
    internal const int ThumbnailPixels = 360;

    /// <summary>The shape a card's thumbnail has when the provider does not say: 4:3.</summary>
    internal const double DefaultAspect = 4.0 / 3;

    // The tallest and widest a thumbnail may be, so one odd picture cannot take over the column.
    private const double MinAspect = 0.5;
    private const double MaxAspect = 2.2;

    private static readonly Color[] MarkColors =
    [
        Color.FromRgb(0xE5, 0x48, 0x4D), Color.FromRgb(0x3E, 0x7B, 0xFA), Color.FromRgb(0x30, 0xA4, 0x6C), Color.FromRgb(0xF7, 0x6B, 0x15),
        Color.FromRgb(0x8E, 0x4E, 0xC6), Color.FromRgb(0x12, 0xA5, 0x94), Color.FromRgb(0xD6, 0x40, 0x9F), Color.FromRgb(0xAB, 0x6E, 0x00),
    ];

    /// <summary>Creates the card for <paramref name="result"/>.</summary>
    public ImageSearchCardViewModel(ImageSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Title = result.Title;
        SourceName = result.SourceName;
        Url = result.PageUrl;
        var decoded = Decode(result.Thumbnail);
        Thumbnail = decoded;
        AspectRatio = result.ThumbnailSize is { Width: > 0, Height: > 0 } size
            ? Math.Clamp((double)size.Width / size.Height, MinAspect, MaxAspect)
            : decoded is { PixelHeight: > 0 }
                ? Math.Clamp((double)decoded.PixelWidth / decoded.PixelHeight, MinAspect, MaxAspect)
                : DefaultAspect;
        MarkLetter = SourceName.Trim() is { Length: > 0 } name ? char.ToUpperInvariant(name[0]).ToString() : "?";
        MarkBrush = Mark(SourceName);
    }

    /// <summary>What the page calls the picture.</summary>
    public string Title { get; }

    /// <summary>The site it is on.</summary>
    public string SourceName { get; }

    /// <summary>The page it is on, or <see langword="null"/> for a sample.</summary>
    public Uri? Url { get; }

    /// <summary>Whether a click opens a page.</summary>
    public bool CanOpen => Url is not null;

    /// <summary>The thumbnail, decoded small and frozen, or <see langword="null"/> when there is none or it could not be read.</summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>The thumbnail's width divided by its height, within sensible limits.</summary>
    public double AspectRatio { get; }

    /// <summary>The first letter of the site's name, which its mark shows.</summary>
    public string MarkLetter { get; }

    /// <summary>The color of the site's mark, the same for the same name.</summary>
    public Brush MarkBrush { get; }

    /// <summary>What assistive technology reads for the card.</summary>
    public string AutomationName => CanOpen ? $"{Title}, {SourceName}" : $"{Title}, {SourceName}, a sample";

    // A thumbnail the provider gave, decoded no larger than a card needs. Anything that is not a picture is no thumbnail.
    private static BitmapSource? Decode(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(data.ToArray());
            bitmap.DecodePixelWidth = ThumbnailPixels;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException
            or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static Brush Mark(string name)
    {
        var hash = 17;
        foreach (var character in name)
        {
            hash = unchecked(hash * 31 + character);
        }

        var brush = new SolidColorBrush(MarkColors[(hash & int.MaxValue) % MarkColors.Length]);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// What the image search results window shows (PROJECT_SPEC §4.6): what the search found as cards, headed "Results from" the provider or,
/// for a made-up sample, saying so. A click on a card opens its page in the user's browser; nothing else leaves the window.
/// </summary>
public sealed class ImageSearchResultsViewModel
{
    private readonly IUrlLauncher? _urls;
    private readonly Uri? _reportUrl;

    /// <summary>Creates the view of <paramref name="results"/>; <paramref name="urls"/> opens a page, when there is one.</summary>
    public ImageSearchResultsViewModel(ImageSearchResults results, IUrlLauncher? urls = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        _urls = urls;
        _reportUrl = results.ReportUrl;
        IsSample = results.IsSample;
        ProviderName = results.ProviderName;
        Cards = [.. results.Results.Select(result => new ImageSearchCardViewModel(result))];
        OpenCommand = new RelayCommand(card =>
        {
            if (card is ImageSearchCardViewModel { Url: { } url })
            {
                _urls?.Open(url);
            }
        });
        ReportCommand = new RelayCommand(_ =>
        {
            if (_reportUrl is not null)
            {
                _urls?.Open(_reportUrl);
            }
        }, _ => CanReport);
    }

    /// <summary>The provider that searched.</summary>
    public string ProviderName { get; }

    /// <summary>Whether these are made-up samples, because no search provider is set up.</summary>
    public bool IsSample { get; }

    /// <summary>The window's title: "Results from Google", or, for a sample, "Sample results".</summary>
    public string Heading => IsSample ? "Sample results" : $"Results from {ProviderName}";

    /// <summary>What a sample says of itself, or an empty string for real results.</summary>
    public string Notice => IsSample ? "Made-up samples: nothing was sent." : string.Empty;

    /// <summary>Whether there is a <see cref="Notice"/>.</summary>
    public bool HasNotice => IsSample;

    /// <summary>The cards, best match first.</summary>
    public IReadOnlyList<ImageSearchCardViewModel> Cards { get; }

    /// <summary>Whether anything was found.</summary>
    public bool HasResults => Cards.Count > 0;

    /// <summary>What the window says when nothing was found.</summary>
    public string EmptyText => "No matches were found.";

    /// <summary>Opens a card's page (its parameter is the card) in the browser, when it has one.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Whether the provider gave a page to report a concern on.</summary>
    public bool CanReport => _reportUrl is not null;

    /// <summary>Opens the provider's page for reporting a concern, in the browser.</summary>
    public ICommand ReportCommand { get; }

    /// <summary>What "Report a Concern" says when it cannot be used: why not.</summary>
    public string ReportHint => CanReport ? "Report a concern about these results to the search provider." : "There is no page to report these results on.";
}
