using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Assistant.UI.Messages;

/// <summary>
/// One image of an <see cref="ImageCollection"/>: its name, and a thumbnail just large enough for a gallery tile. An
/// image file is decoded in the background the first time its thumbnail is asked for, so a gallery appears at once
/// and fills in; until then, or if the file cannot be read, its tile stays empty.
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    /// <summary>
    /// The length, in pixels, that a thumbnail's shorter side is decoded to: a 128 DIP gallery tile at 250 % scale.
    /// </summary>
    public const int ThumbnailSize = 320;

    private ImageSource? _thumbnail;
    private bool _loadStarted;

    /// <summary>Creates an image read from the file at <paramref name="path"/>.</summary>
    /// <param name="name">What the image is called, such as its file name.</param>
    /// <param name="path">The image file's full path.</param>
    public ImageItem(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Name = name;
        Path = path;
    }

    /// <summary>Creates an image whose thumbnail is already in memory.</summary>
    /// <param name="name">What the image is called.</param>
    /// <param name="thumbnail">The image to show. It is frozen if it is not already.</param>
    public ImageItem(string name, ImageSource thumbnail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(thumbnail);
        Name = name;
        _thumbnail = thumbnail.IsFrozen || !thumbnail.CanFreeze ? thumbnail : (ImageSource)thumbnail.GetAsFrozen();
        _loadStarted = true;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Makes the image of a part of the screen that the user captured (PROJECT_SPEC §4.6): a PNG that exists in memory only. It has
    /// no file, so nothing is read for it and nothing about it is saved but that it was a screenshot; its <see cref="Name"/> is only
    /// what the chip and the tile say.
    /// </summary>
    /// <param name="name">What it is called, such as "Screenshot".</param>
    /// <param name="png">The encoded picture, which is what the model is given.</param>
    /// <param name="thumbnail">A small picture of it for the chip and the tile.</param>
    /// <param name="pixelWidth">The width of the captured region, in pixels.</param>
    /// <param name="pixelHeight">The height of the captured region, in pixels.</param>
    public static ImageItem FromCapture(string name, ReadOnlyMemory<byte> png, ImageSource thumbnail, int pixelWidth, int pixelHeight)
    {
        if (png.IsEmpty)
        {
            throw new ArgumentException("A capture has pixels.", nameof(png));
        }

        return new ImageItem(name, thumbnail) { Data = png, PixelWidth = pixelWidth, PixelHeight = pixelHeight, ContextId = Guid.NewGuid() };
    }

    /// <summary>
    /// Makes the image of a picture the user pasted from the clipboard: a PNG that exists in memory only. It has no file, so nothing is read for it,
    /// and it is not a part of the screen: it goes with the one question it was pasted into, like a picture that was attached from a file.
    /// </summary>
    /// <param name="name">What it is called on its chip, such as "Pasted image".</param>
    /// <param name="png">The encoded picture, which is what the model is given.</param>
    /// <param name="thumbnail">A small picture of it for the chip and the message.</param>
    /// <param name="pixelWidth">Its width, in pixels.</param>
    /// <param name="pixelHeight">Its height, in pixels.</param>
    public static ImageItem FromPasted(string name, ReadOnlyMemory<byte> png, ImageSource thumbnail, int pixelWidth, int pixelHeight)
    {
        if (png.IsEmpty)
        {
            throw new ArgumentException("A pasted picture has pixels.", nameof(png));
        }

        return new ImageItem(name, thumbnail) { Data = png, PixelWidth = pixelWidth, PixelHeight = pixelHeight };
    }

    /// <summary>Whether this is a picture that was pasted: it is in memory, and is not a part of the screen.</summary>
    public bool IsPasted => !IsCapture && Path is null && !Data.IsEmpty;

    /// <summary>What the image is called. It names the gallery tile for assistive technology.</summary>
    public string Name { get; }

    /// <summary>The image file's full path, or <see langword="null"/> for an image in memory.</summary>
    public string? Path { get; }

    /// <summary>The encoded picture of a capture or of a pasted picture, or empty for an image that is a file (or a capture that was let go of).</summary>
    public ReadOnlyMemory<byte> Data { get; private set; }

    /// <summary>Whether this is a part of the screen that was captured, which lives in memory only (<see cref="FromCapture"/>).</summary>
    public bool IsCapture => ContextId != Guid.Empty;

    /// <summary>The id of the context item that holds the capture's picture in the context service; empty for an image that is a file.</summary>
    public Guid ContextId { get; private init; }

    /// <summary>The width of a capture, in pixels; 0 for any other image.</summary>
    public int PixelWidth { get; private init; }

    /// <summary>The height of a capture, in pixels; 0 for any other image.</summary>
    public int PixelHeight { get; private init; }

    /// <summary>
    /// Whether a capture has been asked about: the question's message shows its picture, and the later questions of the conversation,
    /// which carry it without showing it again, do not.
    /// </summary>
    public bool WasAsked { get; set; }

    /// <summary>
    /// Lets go of a capture's picture: its bytes are wiped, as far as they are an array of its own, so that what the screen showed does
    /// not linger in memory until the collector gets to it, and the item holds nothing. The thumbnail stays until the item is dropped.
    /// </summary>
    public void Release()
    {
        if (IsCapture && MemoryMarshal.TryGetArray(Data, out var bytes) && bytes.Array is { } array)
        {
            Array.Clear(array, bytes.Offset, bytes.Count);
        }

        Data = default;
    }

    /// <summary>
    /// The thumbnail, or <see langword="null"/> while it is being decoded or when the file cannot be read. Asking for
    /// it starts decoding the file; <see cref="PropertyChanged"/> reports it when it is ready, on the thread that asked.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            StartLoading();
            return _thumbnail;
        }
    }

    private void StartLoading()
    {
        if (_loadStarted || Path is not { } path)
        {
            return;
        }

        _loadStarted = true;
        var context = SynchronizationContext.Current;
        Task.Run(() => ImageThumbnail.Load(path, ThumbnailSize)).ContinueWith(task =>
        {
            if (task.Result is not { } thumbnail)
            {
                return;
            }

            if (context is null)
            {
                SetThumbnail(thumbnail);
            }
            else
            {
                context.Post(_ => SetThumbnail(thumbnail), null);
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void SetThumbnail(ImageSource thumbnail)
    {
        _thumbnail = thumbnail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
}
