using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Controls;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;

namespace Assistant.UI.Capture;

/// <summary>
/// The Visual Intelligence overlay on one monitor (PROJECT_SPEC §4.6): a window over the whole monitor showing the snapshot taken when
/// the capture started, dimmed, in which the user drags a rectangle, adjusts it by its handles and edges, and, once there is one,
/// has the action chips beside it. Esc and the right button give up; Enter in the Ask chip asks. The window holds the snapshot's
/// pixels only as a picture to draw, and crops the selection out of the snapshot it was given, never from the screen again.
/// </summary>
internal sealed partial class CaptureOverlayWindow : Window
{
    private readonly CapturedImage _snapshot;
    private readonly CaptureSelection _selection;
    private bool _covering;

    /// <summary>Creates the overlay for one monitor's <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The monitor as it was a moment ago. The window crops selections out of it, and does not dispose it.</param>
    /// <param name="imageSearch">Whether Image Search can be used.</param>
    public CaptureOverlayWindow(CapturedImage snapshot, ChipAvailability imageSearch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(imageSearch);
        InitializeComponent();
        _snapshot = snapshot;

        // The picture is drawn one pixel for one pixel of the monitor: the window has the monitor's physical size.
        var scale = snapshot.Monitor?.Scale ?? 1;
        Width = snapshot.Width / scale;
        Height = snapshot.Height / scale;
        Surface.Snapshot = SnapshotImage.From(snapshot);
        _selection = new CaptureSelection(new Size(Width, Height));
        Surface.Selection = _selection;
        FrostedPill.SetBackdropSource(Chips, Surface);
        Chips.SetImageSearch(imageSearch);

        Surface.DragStarted += (_, _) =>
        {
            Chips.Visibility = Visibility.Collapsed;
            SelectionStarted?.Invoke(this, EventArgs.Empty);
        };
        Surface.DragCompleted += (_, _) => ShowChips();
        Surface.SecondaryClicked += OnSecondaryClicked;
        Surface.SelectionChanged += (_, _) =>
        {
            if (_selection.IsDragging)
            {
                Chips.Visibility = Visibility.Collapsed;
            }
        };
        Chips.AskRequested += (_, question) => Request(CaptureAction.Ask, question);
        Chips.CopyRequested += (_, _) => Request(CaptureAction.Copy, string.Empty);
        Chips.ImageSearchRequested += (_, _) => Request(CaptureAction.ImageSearch, string.Empty);
        Deactivated += (_, _) => DeactivatedWindow?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the user starts a drag here: the selections on other monitors go.</summary>
    public event EventHandler? SelectionStarted;

    /// <summary>Raised when the user chooses an action for the selection.</summary>
    public event EventHandler<CaptureRequest>? ActionRequested;

    /// <summary>Raised when the user gives up: Esc, or the right button with nothing selected.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Raised when the window stops being the active one.</summary>
    public event EventHandler? DeactivatedWindow;

    /// <summary>The monitor this window covers.</summary>
    public CapturedImage Snapshot => _snapshot;

    /// <summary>The selection, in the window's device-independent pixels.</summary>
    internal CaptureSelection Selection => _selection;

    /// <summary>The surface that draws the snapshot and the selection.</summary>
    internal CaptureSurface SelectionSurface => Surface;

    /// <summary>The chips beside the selection.</summary>
    internal CaptureChips ActionChips => Chips;

    /// <summary>Whether a selection is drawn here.</summary>
    public bool HasSelection => _selection.HasSelection;

    /// <summary>Selects <paramref name="area"/>, in the window's device-independent pixels, and shows the chips beside it.</summary>
    internal void Select(Rect area)
    {
        _selection.Select(area);
        ShowChips();
    }

    /// <summary>
    /// Takes the snapshot's picture off the surface, so that the window, which may be collected later than it is closed, no longer
    /// holds a copy of the whole monitor.
    /// </summary>
    internal void ReleasePicture() => Surface.Snapshot = null;

    /// <summary>Takes the selection away, and the chips with it.</summary>
    public void ClearSelection()
    {
        _selection.Clear();
        Chips.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The selected part of the snapshot as a picture of its own, in the screen's coordinates, or <see langword="null"/> with no
    /// selection. The caller owns it.
    /// </summary>
    public CapturedImage? CropSelection()
    {
        var pixels = Surface.PixelSelection;
        if (pixels.IsEmpty)
        {
            return null;
        }

        var bounds = _snapshot.Bounds;
        var region = new ScreenRect(
            bounds.Left + pixels.X, bounds.Top + pixels.Y, bounds.Left + pixels.X + pixels.Width, bounds.Top + pixels.Y + pixels.Height);
        try
        {
            return _snapshot.Crop(region);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Puts the window over its monitor, above everything else, and has Windows draw it as an overlay. Call it before showing it.</summary>
    internal void CoverMonitor()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        ScreenOverlayWindows.Prepare(handle);
        ScreenOverlayWindows.Cover(handle, _snapshot.Bounds);
        _covering = true;
    }

    /// <summary>Makes this the window the keyboard goes to, with the pointer's crosshair ready.</summary>
    internal void TakeFocus()
    {
        Activate();
        Surface.Focus();
        Keyboard.Focus(Surface);
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Moving onto a monitor with another DPI makes a window rescale itself: it is put back over the monitor once it is shown.
        if (_covering && new WindowInteropHelper(this).Handle is var handle and not 0)
        {
            ScreenOverlayWindows.Cover(handle, _snapshot.Bounds);
        }
    }

    // The right button takes a selection away, so another can be drawn; with none it gives up.
    private void OnSecondaryClicked(object? sender, EventArgs e)
    {
        if (_selection.HasSelection)
        {
            ClearSelection();
        }
        else
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // The chips go beside the selection once the drag ends, and the keyboard goes to the Ask chip so that a question can be typed at once.
    private void ShowChips()
    {
        if (!_selection.HasSelection)
        {
            Chips.Visibility = Visibility.Collapsed;
            return;
        }

        Chips.Visibility = Visibility.Visible;
        Chips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var placement = ChipPlacement.Place(Surface.SnappedSelection, Chips.DesiredSize, _selection.Bounds);
        Canvas.SetLeft(Chips, placement.Position.X);
        Canvas.SetTop(Chips, placement.Position.Y);
        Chips.FocusAsk();
    }

    private void Request(CaptureAction action, string question)
    {
        if (_selection.HasSelection)
        {
            ActionRequested?.Invoke(this, new CaptureRequest(action, question.Trim()));
        }
    }
}

/// <summary>What the user asked of the selection on one monitor.</summary>
/// <param name="Action">What to do with it.</param>
/// <param name="Question">What was typed in the Ask chip, trimmed.</param>
internal sealed record CaptureRequest(CaptureAction Action, string Question);

/// <summary>A snapshot as a picture WPF can draw.</summary>
internal static class SnapshotImage
{
    /// <summary>Makes a frozen bitmap of the monitor's pixels, one for one at 96 DPI, so that the window draws them as they are.</summary>
    public static BitmapSource From(CapturedImage snapshot)
    {
        // The pixels are an array the capture owns; the bitmap takes a copy of them, so wiping the capture leaves the picture alone.
        var pixels = MemoryMarshal.TryGetArray(snapshot.Pixels, out var segment) && segment.Offset == 0
            ? segment.Array!
            : snapshot.Pixels.ToArray();
        var bitmap = BitmapSource.Create(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgr32, null, pixels, snapshot.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Makes a frozen bitmap of the picture scaled down so that its shorter side is <paramref name="shortSide"/> pixels, or as it is when smaller.</summary>
    public static BitmapSource Thumbnail(CapturedImage image, int shortSide)
    {
        var bitmap = From(image);
        var scale = (double)shortSide / Math.Min(image.Width, image.Height);
        if (scale >= 1)
        {
            return bitmap;
        }

        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
