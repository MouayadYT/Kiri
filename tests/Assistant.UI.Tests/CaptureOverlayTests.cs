using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Capture;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The Visual Intelligence overlay (PROJECT_SPEC §4.6) -------------------------------------------------------------------

    private static readonly DateTimeOffset CaptureTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // A snapshot whose every pixel says where it is on the virtual screen, so a crop is checked pixel for pixel.
    private static CapturedImage CodedSnapshot(int width, int height, int left = 0, int top = 0, int dpi = 96, int index = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                BitConverter.TryWriteBytes(pixels.AsSpan((y * width + x) * 4), CodedPixel(left + x, top + y));
            }
        }

        var bounds = new ScreenRect(left, top, left + width, top + height);
        return new CapturedImage(
            CaptureKind.Monitor, CaptureMethod.ScreenCopy, bounds, new CaptureMonitor(index, bounds, dpi, index == 0), pixels, CaptureTime);
    }

    private static uint CodedPixel(int x, int y) => 0xFF000000u | (uint)(((x >> 8 ^ y >> 8) & 0xFF) << 16) | (uint)((y & 0xFF) << 8) | (uint)(x & 0xFF);

    private static uint PixelAt(CapturedImage image, int x, int y) => BitConverter.ToUInt32(image.Pixels.Span[(y * image.Stride + x * 4)..]);

    // The overlay window for a snapshot, shown far off screen so a test does not cover the real monitors, and at a scale of its own.
    private static CaptureOverlayWindow ShownOverlay(CapturedImage snapshot, ChipAvailability? imageSearch = null, double? scale = null)
    {
        var window = new CaptureOverlayWindow(snapshot, imageSearch ?? ChipAvailability.ImageSearchUnavailable)
        {
            Topmost = false,
            ShowActivated = false,
            Left = -30000,
            Top = -30000,
        };
        var deviceScale = scale ?? snapshot.Monitor?.Scale ?? 1;
        window.SelectionSurface.DeviceScaleOverride = deviceScale;
        window.Show();

        // Windows rescales a window that lands on a monitor with another DPI: the size is put back to the monitor's, in its own units.
        window.Width = snapshot.Width / deviceScale;
        window.Height = snapshot.Height / deviceScale;
        window.UpdateLayout();
        Pump();
        return window;
    }

    [Fact]
    public void TheOverlayHasTheMonitorsSize_AndShowsItsSnapshotWithNoSelectionAndNoChips() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(1000, 600, dpi: 120);
        var window = ShownOverlay(snapshot);
        try
        {
            // 1000 x 600 pixels at 125 % is 800 x 480 device-independent pixels.
            Assert.Equal((800, 480), (window.Width, window.Height));
            Assert.Equal((800, 480), (window.SelectionSurface.ActualWidth, window.SelectionSurface.ActualHeight));
            Assert.NotNull(window.SelectionSurface.Snapshot);
            Assert.False(window.HasSelection);
            Assert.Equal(Visibility.Collapsed, window.ActionChips.Visibility);
            Assert.Null(window.CropSelection());
            Assert.Equal("Assistant screen capture", window.Title);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void ASelectionTheUserDraws_HasTheChipsBesideIt_AndCropsTheSnapshotPixelForPixel() => RunSta(() => WithTheme(() =>
    {
        // A monitor to the left of and above the primary, at 125 %: the crop's place is in the virtual screen's coordinates.
        using var snapshot = CodedSnapshot(1000, 600, left: -1000, top: -300, dpi: 120);
        var window = ShownOverlay(snapshot);
        try
        {
            var surface = window.SelectionSurface;
            Assert.True(surface.PointerDown(new Point(80, 40)));
            Assert.Equal(Visibility.Collapsed, window.ActionChips.Visibility);
            surface.PointerMoved(new Point(400, 240));
            Assert.True(surface.PointerUp(new Point(400, 240)));
            Pump();

            Assert.True(window.HasSelection);
            Assert.Equal(Visibility.Visible, window.ActionChips.Visibility);

            // Chips to the right, level with the middle, 8 DIPs out.
            var selection = surface.SnappedSelection;
            Assert.Equal(new Rect(80, 40, 320, 200), selection);
            Assert.Equal(selection.Right + ChipPlacement.Gap, Canvas.GetLeft(window.ActionChips), 3);

            using var crop = window.CropSelection()!;
            Assert.Equal(new ScreenRect(-900, -250, -500, 0), crop.Bounds);
            Assert.Equal((400, 250), (crop.Width, crop.Height));
            Assert.Equal(CaptureKind.Region, crop.Kind);
            Assert.Equal(CodedPixel(-900, -250), PixelAt(crop, 0, 0));
            Assert.Equal(CodedPixel(-501, -1), PixelAt(crop, 399, 249));
            Assert.Same(snapshot.Monitor, crop.Monitor);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void WhileTheSelectionIsDragged_TheChipsAreAway_AndComeBackWhenItIsLetGo() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            window.Select(new Rect(100, 100, 300, 200));
            Assert.Equal(Visibility.Visible, window.ActionChips.Visibility);

            // Grabbing a handle, and moving what is selected, takes the chips off until the pointer is released.
            var surface = window.SelectionSurface;
            Assert.True(surface.PointerDown(new Point(400, 300)));
            surface.PointerMoved(new Point(500, 400));
            Assert.Equal(Visibility.Collapsed, window.ActionChips.Visibility);
            Assert.True(surface.PointerUp(new Point(500, 400)));

            Assert.Equal(new Rect(100, 100, 400, 300), surface.SnappedSelection);
            Assert.Equal(Visibility.Visible, window.ActionChips.Visibility);
            Assert.Equal(500 + ChipPlacement.Gap, Canvas.GetLeft(window.ActionChips), 3);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void TheSelectionIsDrawnAndCroppedOnWholeDevicePixels_ThatIsWhatIsSeenIsWhatIsTaken() => RunSta(() => WithTheme(() =>
    {
        // At 125 % a pointer position of 100.4 DIPs is device pixel 125.5: the edge is on a pixel, in the drawing and the crop alike.
        using var snapshot = CodedSnapshot(1000, 600, dpi: 120);
        var window = ShownOverlay(snapshot);
        try
        {
            window.Select(new Rect(100.4, 50.2, 200.3, 100.7));

            var pixels = window.SelectionSurface.PixelSelection;
            var snapped = window.SelectionSurface.SnappedSelection;

            Assert.Equal(new Int32Rect(126, 63, 250, 126), pixels);
            Assert.Equal(new Rect(126 / 1.25, 63 / 1.25, 250 / 1.25, 126 / 1.25), snapped);
            using var crop = window.CropSelection()!;
            Assert.Equal(new ScreenRect(126, 63, 376, 189), crop.Bounds);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void TheAskChipTakesTheKeyboard_AndEnterAsksWithWhatWasTyped_TrimmedAndNothingElse() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            var requests = new List<CaptureRequest>();
            window.ActionRequested += (_, request) => requests.Add(request);
            window.Select(new Rect(100, 100, 300, 200));
            Assert.Same(window.ActionChips.AskField, FocusManager.GetFocusedElement(window));
            Assert.Equal(Visibility.Visible, window.ActionChips.FindName("AskPlaceholder") is TextBlock placeholder ? placeholder.Visibility : Visibility.Collapsed);

            window.ActionChips.Question = "  explain this error  ";
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.ActionChips.FindName("AskPlaceholder")).Visibility);
            PressOverlayKey(window.ActionChips.AskField, Key.Enter);

            Assert.Equal([new CaptureRequest(CaptureAction.Ask, "explain this error")], requests);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void EnterWithNothingTyped_StillAsks_SoTheConversationOpensWithThePictureAttached() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            var requests = new List<CaptureRequest>();
            window.ActionRequested += (_, request) => requests.Add(request);
            window.Select(new Rect(100, 100, 300, 200));

            PressOverlayKey(window.ActionChips.AskField, Key.Enter);

            Assert.Equal([new CaptureRequest(CaptureAction.Ask, "")], requests);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void CopyAsksForCopy_ImageSearchStaysDimmedUntilItCanBeUsed_AndSaysWhy() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            var requests = new List<CaptureRequest>();
            window.ActionRequested += (_, request) => requests.Add(request);
            window.Select(new Rect(100, 100, 300, 200));
            var chips = window.ActionChips;

            Assert.True(chips.CopyChip.IsEnabled);
            Invoke(chips.CopyChip);
            Assert.Equal([new CaptureRequest(CaptureAction.Copy, "")], requests);

            // Without a way to search with an image the chip is disabled, and its tooltip says so.
            Assert.False(chips.ImageSearchChip.IsEnabled);
            Assert.Equal(ChipAvailability.ImageSearchUnavailable.Hint, chips.ImageSearchChip.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(chips.ImageSearchChip));
            Assert.Equal(0.5, ((Grid)chips.ImageSearchChip.Template.FindName("Chip", chips.ImageSearchChip)).Opacity);
            Assert.Single(requests);

            // Once it can be used, it asks like the others do.
            chips.SetImageSearch(new ChipAvailability(true, "Sends the picture to a search provider."));
            Assert.True(chips.ImageSearchChip.IsEnabled);
            Invoke(chips.ImageSearchChip);
            Assert.Equal(new CaptureRequest(CaptureAction.ImageSearch, ""), requests[^1]);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void AChipActionWithNoSelection_AsksForNothing() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            var requests = new List<CaptureRequest>();
            window.ActionRequested += (_, request) => requests.Add(request);
            window.Select(new Rect(100, 100, 300, 200));
            window.ClearSelection();

            Invoke(window.ActionChips.CopyChip);

            Assert.Empty(requests);
            Assert.Equal(Visibility.Collapsed, window.ActionChips.Visibility);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void EscGivesUp_AndTheRightButtonTakesTheSelectionAwayFirstAndGivesUpOnlyWithNone() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = CodedSnapshot(800, 500);
        var window = ShownOverlay(snapshot);
        try
        {
            var cancelled = 0;
            window.CancelRequested += (_, _) => cancelled++;
            window.Select(new Rect(100, 100, 300, 200));

            RightClick(window.SelectionSurface);
            Assert.False(window.HasSelection);
            Assert.Equal(Visibility.Collapsed, window.ActionChips.Visibility);
            Assert.Equal(0, cancelled);

            RightClick(window.SelectionSurface);
            Assert.Equal(1, cancelled);

            window.Select(new Rect(100, 100, 300, 200));
            PressOverlayKey(window.ActionChips.AskField, Key.Escape, preview: true);
            Assert.Equal(2, cancelled);
        }
        finally
        {
            window.Close();
        }
    }));

    // ---- All the monitors together --------------------------------------------------------------------------------------------

    private static (CaptureOverlay Overlay, List<CaptureOverlayWindow> Shown) OffScreenOverlay()
    {
        var shown = new List<CaptureOverlayWindow>();
        var overlay = new CaptureOverlay(window =>
        {
            window.Topmost = false;
            window.ShowActivated = false;
            window.Left = -30000;
            window.Top = -30000;
            window.SelectionSurface.DeviceScaleOverride = window.Snapshot.Monitor?.Scale ?? 1;
            shown.Add(window);
        });
        return (overlay, shown);
    }

    [Fact]
    public void EveryMonitorGetsItsOwnWindow_AndAnActionOnOneEndsTheCapture_WipingEverySnapshot() => RunSta(() => WithTheme(() =>
    {
        var left = CodedSnapshot(600, 400, left: -600, top: 0, dpi: 96, index: 0);
        var right = CodedSnapshot(800, 500, left: 0, top: 0, dpi: 120, index: 1);
        var (overlay, shown) = OffScreenOverlay();

        var task = overlay.SelectAsync([left, right], ChipAvailability.ImageSearchUnavailable, CancellationToken.None);
        Assert.Equal(2, shown.Count);
        Assert.Equal([left, right], shown.Select(window => window.Snapshot));
        Assert.All(shown, window => Assert.True(window.IsVisible));
        Assert.False(task.IsCompleted);

        shown[1].Select(new Rect(50, 40, 200, 100));
        Invoke(shown[1].ActionChips.CopyChip);
        Pump();

        Assert.True(task.IsCompleted);
        using var outcome = task.Result!;
        Assert.Equal(CaptureAction.Copy, outcome.Action);
        Assert.Equal(new ScreenRect(63, 50, 313, 175), outcome.Region.Bounds);
        Assert.Equal(CodedPixel(63, 50), PixelAt(outcome.Region, 0, 0));
        Assert.All(shown, window => Assert.False(window.IsVisible));

        // The snapshots of the whole screen were wiped; the part selected is its own copy.
        Assert.Throws<ObjectDisposedException>(() => left.Pixels);
        Assert.Throws<ObjectDisposedException>(() => right.Pixels);
        Assert.Equal(CodedPixel(63, 50), PixelAt(outcome.Region, 0, 0));
    }));

    [Fact]
    public void StartingASelectionOnAnotherMonitor_TakesTheFirstOnesAway() => RunSta(() => WithTheme(() =>
    {
        var first = CodedSnapshot(600, 400, left: 0, top: 0, index: 0);
        var second = CodedSnapshot(600, 400, left: 600, top: 0, index: 1);
        var (overlay, shown) = OffScreenOverlay();
        var task = overlay.SelectAsync([first, second], ChipAvailability.ImageSearchUnavailable, CancellationToken.None);
        try
        {
            shown[0].Select(new Rect(50, 50, 200, 100));
            Assert.True(shown[0].HasSelection);

            Assert.True(shown[1].SelectionSurface.PointerDown(new Point(100, 100)));

            Assert.False(shown[0].HasSelection);
            Assert.Equal(Visibility.Collapsed, shown[0].ActionChips.Visibility);
            shown[1].SelectionSurface.PointerUp(new Point(300, 250));
            Assert.True(shown[1].HasSelection);
            Assert.False(shown[0].HasSelection);
        }
        finally
        {
            foreach (var window in shown.ToArray())
            {
                window.Close();
            }

            first.Dispose();
            second.Dispose();
        }

        Assert.False(task.IsCompleted);
    }));

    [Fact]
    public void EscOnAnyMonitorGivesUp_WithNoOutcome_AndWipesTheSnapshots() => RunSta(() => WithTheme(() =>
    {
        var first = CodedSnapshot(600, 400, index: 0);
        var second = CodedSnapshot(600, 400, left: 600, index: 1);
        var (overlay, shown) = OffScreenOverlay();
        var task = overlay.SelectAsync([first, second], ChipAvailability.ImageSearchUnavailable, CancellationToken.None);

        PressOverlayKey(shown[1].SelectionSurface, Key.Escape, preview: true, source: shown[1]);
        Pump();

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
        Assert.All(shown, window => Assert.False(window.IsVisible));
        Assert.Throws<ObjectDisposedException>(() => first.Pixels);
        Assert.Throws<ObjectDisposedException>(() => second.Pixels);
    }));

    [Fact]
    public void CancellingTheTokenTakesTheOverlayDown_WithNoOutcome() => RunSta(() => WithTheme(() =>
    {
        var snapshot = CodedSnapshot(600, 400);
        var (overlay, shown) = OffScreenOverlay();
        using var cancel = new CancellationTokenSource();
        var task = overlay.SelectAsync([snapshot], ChipAvailability.ImageSearchUnavailable, cancel.Token);

        cancel.Cancel();
        Pump();

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
        Assert.All(shown, window => Assert.False(window.IsVisible));
        Assert.Throws<ObjectDisposedException>(() => snapshot.Pixels);
    }));

    // ---- What the overlay looks like -------------------------------------------------------------------------------------------

    // A page like the reference's: white, with the dark things the reference shows under its chips, so that the frosted glass has
    // something to blur. Pixels are those of the reference (1400 x 860, drawn at a scale of 2).
    private static CapturedImage ReferencePage()
    {
        const int width = 1400;
        const int height = 860;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);

        void Fill(int left, int top, int right, int bottom, byte gray)
        {
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var at = (y * width + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = gray;
                }
            }
        }

        // The dark thumbnail under the Ask chip, and a line of dark words beside it (the reference reads "Space Background").
        Fill(1107, 377, 1171, 433, 24);
        for (var word = 0; word < 6; word++)
        {
            Fill(1193 + word * 28, 392, 1193 + word * 28 + 20, 412, 17);
        }

        // The dark strip along the top of the selection, and the browser's bar above the page.
        Fill(80, 62, 1031, 72, 28);
        Fill(0, 0, 1400, 46, 28);

        var bounds = new ScreenRect(0, 0, width, height);
        return new CapturedImage(
            CaptureKind.Monitor, CaptureMethod.ScreenCopy, bounds, new CaptureMonitor(0, bounds, 192, true), pixels, CaptureTime);
    }

    [Fact]
    public void TheOverlayIsDrawnAsInTheReference_TheScreenDimmedByHalf_TheSelectionClear_WithAGlowAndHandles() => RunSta(() => WithTheme(() =>
    {
        using var snapshot = ReferencePage();
        var window = ShownOverlay(snapshot, scale: 2);
        try
        {
            // The reference's selection, in its device pixels at 2x: (80, 60) to (1031, 797).
            window.Select(new Rect(40.15, 29.85, 475.45, 368.65));
            Pump();
            var page = new Rect(0, 0, 700, 430);
            RenderFixture((FrameworkElement)window.FindName("Root"), "capture-overlay-2x.png", 2, page);

            var picture = RenderPixels((FrameworkElement)window.FindName("Root"), 2, page);
            Assert.Equal((1400, 860), (picture.Width, picture.Height));

            // Outside the selection a white screen reads 128; inside it is white.
            Assert.InRange(picture.Gray(30, 500), 127, 129);
            Assert.InRange(picture.Gray(1300, 150), 127, 129);
            Assert.Equal(255, picture.Gray(500, 400));
            Assert.Equal(255, picture.Gray(300, 150));

            // The glow outside the selection's left edge falls off as the reference's does: 169, 155, 142, 136, 132, 130 at one to six
            // pixels out, from a screen of 128.
            var reference = new[] { 169, 155, 142, 136, 132, 130 };
            for (var pixel = 0; pixel < reference.Length; pixel++)
            {
                Assert.InRange(picture.Gray(79 - pixel, 300), reference[pixel] - 7, reference[pixel] + 7);
            }

            // The selection's own edge is on whole pixels: 80 is the first clear one.
            Assert.Equal(255, picture.Gray(80, 300));

            // A handle is a white disc 12 pixels across, centered on the corner, which shows over the dimmed screen.
            Assert.InRange(picture.Gray(77, 57), 249, 261);
            Assert.InRange(picture.Gray(83, 57), 249, 261);
            Assert.InRange(picture.Gray(73, 50), 126, 130);
            Assert.InRange(picture.Gray(556, 57), 249, 261);
            Assert.InRange(picture.Gray(556, 50), 126, 130);
        }
        finally
        {
            window.Close();
        }
    }));

    // The pixels of an element drawn on its own at a scale, as gray levels.
    private static (int Width, int Height, Func<int, int, int> Gray) RenderPixels(FrameworkElement element, double scale, Rect? region = null)
    {
        var bitmap = Render(element, scale, region);
        var width = bitmap.PixelWidth;
        var pixels = new byte[width * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (width, bitmap.PixelHeight, (x, y) =>
        {
            var at = (y * width + x) * 4;
            return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3;
        });
    }

    private static void Invoke(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));

    private static void RightClick(UIElement element)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
            Source = element,
        };
        element.RaiseEvent(args);
    }

    private static void PressOverlayKey(UIElement element, Key key, bool preview = true, FrameworkElement? source = null)
    {
        var target = (PresentationSource?)PresentationSource.FromVisual(source ?? element) ?? throw new InvalidOperationException("Not shown.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, target, Environment.TickCount, key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent,
            Source = element,
        };
        element.RaiseEvent(args);
    }
}
