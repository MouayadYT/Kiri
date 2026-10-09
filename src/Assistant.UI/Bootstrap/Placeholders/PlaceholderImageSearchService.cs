using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Contracts;
using Assistant.Core.ImageSearch;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Stands in for an image search provider until one is set up (PROJECT_SPEC §4.6): it makes up a few sample results, with thumbnails it
/// draws itself, and sends nothing anywhere, which it says (<see cref="IImageSearchService.IsSample"/>). It is the only
/// <see cref="IImageSearchService"/> there is; the core knows only the contract, so a real provider replaces this registration and
/// nothing else changes. It still goes through the whole flow: Local Only mode, the permission, and the consent the flow issues.
/// </summary>
internal sealed class PlaceholderImageSearchService(TimeProvider clock) : IImageSearchService
{
    /// <summary>What the sample's provider is called.</summary>
    internal const string Name = "Sample";

    // The sample matches: a title, a site, and the shape and color of the thumbnail drawn for it.
    private static readonly (string Title, string Site, int Width, int Height, uint From, uint To)[] Samples =
    [
        ("Sample page about a picture", "example.org", 640, 480, 0xFF1F6F8B, 0xFF99D1D8),
        ("A longer sample title that goes on to a second line", "Sample Site", 360, 600, 0xFF6C3F9E, 0xFFD6B3F2),
        ("Sample gallery of similar pictures", "gallery.example", 640, 360, 0xFFB5532B, 0xFFF3C29B),
        ("Another sample result", "Example News", 480, 480, 0xFF2E7D4F, 0xFFA8DDB5),
        ("Sample video: how this works", "Sample Video", 640, 360, 0xFFA3263A, 0xFFF0A1AC),
        ("Sample article with a picture", "blog.example", 480, 640, 0xFF3B4FA8, 0xFFB4C0F2),
    ];

    /// <inheritdoc/>
    public string ProviderName => Name;

    /// <inheritdoc/>
    public bool IsSample => true;

    /// <inheritdoc/>
    /// <remarks>Draws its thumbnails on the calling (UI) thread, which takes a moment for six small pictures.</remarks>
    public Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Consent.Consume(request.Image, clock.GetUtcNow(), sendsImageOffPc: false);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSample());
    }

    /// <summary>Makes the sample results: made-up matches, with no page to open. Nothing is searched and nothing is sent.</summary>
    internal static ImageSearchResults CreateSample()
    {
        var results = Samples
            .Select(sample => new ImageSearchResult(sample.Title, sample.Site, null, Thumbnail(sample.Width, sample.Height, sample.From, sample.To))
            {
                ThumbnailSize = (sample.Width, sample.Height),
            })
            .ToList();
        return new ImageSearchResults(Name, IsSample: true, results);
    }

    // A soft diagonal wash from one color to another with a lighter circle in it, as a PNG: nothing of the user's.
    private static byte[] Thumbnail(int width, int height, uint from, uint to)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var wash = new LinearGradientBrush(ColorOf(from), ColorOf(to), new Point(0, 0), new Point(1, 1));
            context.DrawRectangle(wash, null, new Rect(0, 0, width, height));
            var circle = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            context.DrawEllipse(circle, null, new Point(width * 0.62, height * 0.4), Math.Min(width, height) * 0.22, Math.Min(width, height) * 0.22);
            context.DrawEllipse(circle, null, new Point(width * 0.3, height * 0.7), Math.Min(width, height) * 0.12, Math.Min(width, height) * 0.12);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color ColorOf(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
