using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Capturing a monitor, a window or a rectangle of the screen into memory (PROJECT_SPEC §4.6): what is captured, the metadata that
/// goes with it, what is refused, and that nothing about the screen reaches a log. The screen is a fake here; the real one is tested
/// in <see cref="RealScreenCaptureTests"/>.
/// </summary>
public sealed class ScreenCaptureServiceTests
{
    // The layout of the development PC: a 125 % primary, a 125 % monitor to its right, and a 100 % portrait one to the left, which
    // lies at negative coordinates.
    private static readonly CaptureMonitor Portrait = new(0, new ScreenRect(-1080, -491, 0, 1429), 96, false);
    private static readonly CaptureMonitor Primary = new(1, new ScreenRect(0, 0, 2560, 1440), 120, true);
    private static readonly CaptureMonitor Right = new(2, new ScreenRect(2560, 0, 5120, 1440), 120, false);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeScreen _screen = new([Portrait, Primary, Right]);
    private readonly TestLogger _logger = new();

    private ScreenCaptureService Service => new(_logger, new FixedClock(Now), _screen);

    [Fact]
    public async Task AMonitor_IsCaptured_WithItsOwnBoundsAndMetadata()
    {
        using var image = await Service.CaptureMonitorAsync(Primary);

        Assert.Equal(CaptureKind.Monitor, image.Kind);
        Assert.Equal(CaptureMethod.ScreenCopy, image.Method);
        Assert.Equal(Primary.Bounds, image.Bounds);
        Assert.Equal((2560, 1440), (image.Width, image.Height));
        Assert.Equal(2560 * 4, image.Stride);
        Assert.Same(Primary, image.Monitor);
        Assert.Equal(Now, image.CapturedAt);
        Assert.Equal(2560 * 1440 * 4, image.Pixels.Length);
        Assert.Equal([Primary.Bounds], _screen.Copied);
    }

    [Fact]
    public async Task EveryMonitor_IsCapturedAtOnce_OneImageEach_InTheMonitorsOrder()
    {
        var images = await Service.CaptureAllMonitorsAsync();

        Assert.Equal([Portrait, Primary, Right], images.Select(image => image.Monitor));
        Assert.Equal([Portrait.Bounds, Primary.Bounds, Right.Bounds], images.Select(image => image.Bounds));
        Assert.All(images, image => Assert.Equal(CaptureKind.Monitor, image.Kind));
        foreach (var image in images)
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task APixelOfTheCapture_IsThePixelOfTheScreenAtThatPlace_WhateverTheMonitorsOffset()
    {
        // The fake paints each pixel with its own screen position, so a capture at negative coordinates is checked pixel for pixel.
        using var image = await Service.CaptureRegionAsync(new ScreenRect(-30, -20, 50, 40));

        Assert.Equal((80, 60), (image.Width, image.Height));
        Assert.Equal(FakeScreen.PixelAt(-30, -20), PixelOf(image, 0, 0));
        Assert.Equal(FakeScreen.PixelAt(49, 39), PixelOf(image, 79, 59));
        Assert.Equal(FakeScreen.PixelAt(0, 0), PixelOf(image, 30, 20));
        Assert.Equal(CaptureKind.Region, image.Kind);
    }

    [Fact]
    public async Task ARegionOnSeveralMonitors_IsOnePicture_AndItsMonitorIsTheOneHoldingMostOfIt()
    {
        using var image = await Service.CaptureRegionAsync(new ScreenRect(2400, 100, 3000, 300));

        Assert.Equal((600, 200), (image.Width, image.Height));
        Assert.Same(Right, image.Monitor);
        Assert.Equal([new ScreenRect(2400, 100, 3000, 300)], _screen.Copied);
    }

    [Fact]
    public async Task ARegionPartlyOffTheScreen_IsCutToTheScreen_AndSaysSo()
    {
        using var image = await Service.CaptureRegionAsync(new ScreenRect(5000, 1400, 5300, 1600));

        Assert.Equal(new ScreenRect(5000, 1400, 5120, 1440), image.Bounds);
        Assert.Equal((120, 40), (image.Width, image.Height));
    }

    [Theory]
    [InlineData(6000, 0, 6100, 100)]
    [InlineData(100, 100, 100, 200)]
    [InlineData(100, 100, 200, 100)]
    [InlineData(300, 300, 200, 200)]
    public async Task ARegionWithNothingOnTheScreen_IsRefusedAsNothingToCapture(int left, int top, int right, int bottom)
    {
        var failure = await Assert.ThrowsAsync<ScreenCaptureException>(
            () => Service.CaptureRegionAsync(new ScreenRect(left, top, right, bottom)));

        Assert.Equal(ScreenCaptureFailure.NothingToCapture, failure.Failure);
        Assert.Empty(_screen.Copied);
    }

    [Fact]
    public async Task APictureLargerThanTheLimit_IsRefusedBeforeAnyMemoryIsTaken()
    {
        var huge = new FakeScreen([new CaptureMonitor(0, new ScreenRect(0, 0, 20000, 20000), 96, true)]);
        var service = new ScreenCaptureService(_logger, new FixedClock(Now), huge);

        var failure = await Assert.ThrowsAsync<ScreenCaptureException>(
            () => service.CaptureRegionAsync(new ScreenRect(0, 0, 20000, 20000)));

        Assert.Equal(ScreenCaptureFailure.TooLarge, failure.Failure);
        Assert.Empty(huge.Copied);
    }

    [Fact]
    public async Task WhenWindowsRefusesToCopyTheScreen_TheFailureNamesItsErrorCodeAndNothingElse()
    {
        _screen.CopyError = 5;

        var failure = await Assert.ThrowsAsync<ScreenCaptureException>(() => Service.CaptureMonitorAsync(Primary));

        Assert.Equal((ScreenCaptureFailure.Refused, 5), (failure.Failure, failure.ErrorCode));
        Assert.Contains(_logger.Messages, line => line.Contains("Refused") && line.Contains('5'));
    }

    [Fact]
    public async Task ACancelledCapture_CopiesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CaptureMonitorAsync(Primary, cancelled.Token));

        Assert.Empty(_screen.Copied);
    }

    [Fact]
    public async Task OneMonitorFailing_FailsTheSnapshot_AndWipesTheOthersPixels()
    {
        _screen.FailFor = Right.Bounds;
        _screen.CopyError = 5;

        await Assert.ThrowsAsync<ScreenCaptureException>(() => Service.CaptureAllMonitorsAsync());

        Assert.All(_screen.Delivered, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
        Assert.NotEmpty(_screen.Delivered);
    }

    [Fact]
    public async Task AWindow_IsRenderedByItself_AndCutToWhatIsSeenOfIt()
    {
        // Windows gives a window an invisible resize border: the window renders into its outer rectangle, and the picture is the
        // visible part.
        var outer = new ScreenRect(100, 100, 700, 500);
        var visible = new ScreenRect(107, 100, 693, 493);
        _screen.Windows[77] = new WindowFrame(outer, visible);

        using var image = await Service.CaptureWindowAsync(77);

        Assert.Equal(CaptureKind.Window, image.Kind);
        Assert.Equal(CaptureMethod.WindowRender, image.Method);
        Assert.Equal(visible, image.Bounds);
        Assert.Equal((586, 393), (image.Width, image.Height));
        Assert.Equal(FakeScreen.PixelAt(107, 100), PixelOf(image, 0, 0));
        Assert.Equal(FakeScreen.PixelAt(692, 492), PixelOf(image, 585, 392));
        Assert.Same(Primary, image.Monitor);
        Assert.Empty(_screen.Copied);
        Assert.Equal([(77, 600, 400)], _screen.Rendered);
    }

    [Fact]
    public async Task AWindowThatWillNotDrawItself_IsCapturedFromTheScreenWhereItIs()
    {
        var frame = new ScreenRect(200, 200, 500, 400);
        _screen.Windows[77] = new WindowFrame(frame, frame);
        _screen.RenderError = 6;

        using var image = await Service.CaptureWindowAsync(77);

        Assert.Equal((CaptureKind.Window, CaptureMethod.ScreenCopy), (image.Kind, image.Method));
        Assert.Equal(frame, image.Bounds);
        Assert.Equal([frame], _screen.Copied);
    }

    [Fact]
    public async Task AWindowThatIsGoneOrMinimized_IsRefused()
    {
        var failure = await Assert.ThrowsAsync<ScreenCaptureException>(() => Service.CaptureWindowAsync(99));

        Assert.Equal(ScreenCaptureFailure.WindowUnavailable, failure.Failure);
        Assert.Empty(_screen.Rendered);
    }

    [Fact]
    public async Task ACrop_IsACopyOfTheRegion_InTheSnapshotsOwnCoordinates()
    {
        using var snapshot = await Service.CaptureMonitorAsync(Right);

        using var crop = snapshot.Crop(new ScreenRect(2600, 40, 2700, 90));

        Assert.Equal(CaptureKind.Region, crop.Kind);
        Assert.Equal(new ScreenRect(2600, 40, 2700, 90), crop.Bounds);
        Assert.Equal(Now, crop.CapturedAt);
        Assert.Same(Right, crop.Monitor);
        Assert.Equal(FakeScreen.PixelAt(2600, 40), PixelOf(crop, 0, 0));
        Assert.Equal(FakeScreen.PixelAt(2699, 89), PixelOf(crop, 99, 49));
        Assert.Equal(100 * 50 * 4, crop.Pixels.Length);

        // It is a copy: wiping the snapshot leaves it as it is.
        snapshot.Dispose();
        Assert.Equal(FakeScreen.PixelAt(2600, 40), PixelOf(crop, 0, 0));
    }

    [Fact]
    public async Task ACropHangingOutOfTheSnapshot_IsCutToIt_AndOneWhollyOutsideIsRefused()
    {
        using var snapshot = await Service.CaptureMonitorAsync(Right);

        using var crop = snapshot.Crop(new ScreenRect(5100, 1400, 5200, 1500));

        Assert.Equal(new ScreenRect(5100, 1400, 5120, 1440), crop.Bounds);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Crop(new ScreenRect(0, 0, 100, 100)));
    }

    [Fact]
    public async Task ADisposedCapture_HasWipedItsPixels_AndRefusesToGiveThemOut()
    {
        var image = await Service.CaptureRegionAsync(new ScreenRect(10, 10, 20, 20));
        var pixels = _screen.Delivered.Single();
        Assert.Contains(pixels, value => value != 0);

        image.Dispose();

        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => image.Pixels);
        Assert.Throws<ObjectDisposedException>(() => image.Crop(new ScreenRect(10, 10, 12, 12)));
    }

    [Fact]
    public void ACaptureWithPixelsOfTheWrongSize_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new CapturedImage(CaptureKind.Region, CaptureMethod.ScreenCopy, new ScreenRect(0, 0, 4, 4), null, new byte[10], Now));
        Assert.Throws<ArgumentException>(
            () => new CapturedImage(CaptureKind.Region, CaptureMethod.ScreenCopy, new ScreenRect(0, 0, 0, 4), null, [], Now));
    }

    [Fact]
    public async Task NothingOfTheScreen_ReachesALog_OnlyKindsSizesAndTimes()
    {
        using var image = await Service.CaptureRegionAsync(new ScreenRect(0, 0, 40, 30));
        using var other = await Service.CaptureMonitorAsync(Right);
        _screen.Windows[5] = new WindowFrame(new ScreenRect(0, 0, 50, 50), new ScreenRect(0, 0, 50, 50));
        using var window = await Service.CaptureWindowAsync(5);

        Assert.NotEmpty(_logger.Messages);
        Assert.All(_logger.Messages, line => Assert.Matches(@"^(Captured an? \w+ by \w+: \d+ x \d+ pixels in \d+ ms|.* capture failed: .*)$", line));
        Assert.DoesNotContain(_logger.Messages, line => line.Contains(PixelOf(image, 1, 1).ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain("pixels = ", image.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CapturedImage { Kind = Region, Width = 40, Height = 30 }", image.ToString());
    }

    [Fact]
    public void TheMonitorsCover_TheVirtualScreen_AndTheLargestShareChoosesTheMonitor()
    {
        var monitors = new[] { Portrait, Primary, Right };

        Assert.Equal(new ScreenRect(-1080, -491, 5120, 1440), ScreenCaptureService.VirtualScreen(monitors));
        Assert.Null(ScreenCaptureService.VirtualScreen([]));
        Assert.Same(Portrait, ScreenCaptureService.LargestShare(new ScreenRect(-200, 0, 50, 100), monitors));
        Assert.Same(Primary, ScreenCaptureService.LargestShare(new ScreenRect(-50, 0, 200, 100), monitors));
        Assert.Null(ScreenCaptureService.LargestShare(new ScreenRect(6000, 0, 6100, 100), monitors));
        Assert.Equal(1.25, Primary.Scale);
        Assert.Equal(1.0, Portrait.Scale);
    }

    [Fact]
    public void OpaqueIsForcedOnEveryPixel_WhateverGdiLeftInTheFourthByte()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 7, 8, 9, 10, 255];

        ScreenCaptureNativeMethods.MakeOpaque(pixels);

        Assert.Equal<byte>([1, 2, 3, 255, 4, 5, 6, 255, 8, 9, 10, 255], pixels);
    }

    // The pixel at a place of the picture, as the number its four bytes make.
    private static uint PixelOf(CapturedImage image, int x, int y) => BitConverter.ToUInt32(image.Pixels.Span[(y * image.Stride + x * 4)..]);

    // A screen where every pixel's value is its own position, so what a capture copied can be told exactly.
    private sealed class FakeScreen(IReadOnlyList<CaptureMonitor> monitors) : IScreenCaptureNativeMethods
    {
        public List<ScreenRect> Copied { get; } = [];
        public List<(nint Window, int Width, int Height)> Rendered { get; } = [];
        public List<byte[]> Delivered { get; } = [];
        public Dictionary<nint, WindowFrame> Windows { get; } = [];
        public int CopyError { get; set; }
        public int RenderError { get; set; }
        public ScreenRect? FailFor { get; set; }
        private readonly object _gate = new();

        public IReadOnlyList<CaptureMonitor> Monitors() => monitors;

        public static uint PixelAt(int x, int y) => 0xFF000000u | (uint)(((x & 0xFFF) << 12) | (y & 0xFFF));

        public int CopyScreen(ScreenRect area, out byte[]? pixels)
        {
            lock (_gate)
            {
                if (CopyError != 0 && (FailFor is null || FailFor == area))
                {
                    pixels = null;
                    return CopyError;
                }

                Copied.Add(area);
            }

            pixels = Fill(area);
            lock (_gate)
            {
                Delivered.Add(pixels);
            }

            return 0;
        }

        public WindowFrame? DescribeWindow(nint window) => Windows.TryGetValue(window, out var frame) ? frame : null;

        public int RenderWindow(nint window, int width, int height, out byte[]? pixels)
        {
            if (RenderError != 0)
            {
                pixels = null;
                return RenderError;
            }

            Rendered.Add((window, width, height));
            var outer = Windows[window].Outer;
            pixels = Fill(outer);
            Delivered.Add(pixels);
            return 0;
        }

        private static byte[] Fill(ScreenRect area)
        {
            var pixels = new byte[area.Width * area.Height * 4];
            for (var y = 0; y < area.Height; y++)
            {
                for (var x = 0; x < area.Width; x++)
                {
                    BitConverter.TryWriteBytes(pixels.AsSpan((y * area.Width + x) * 4), PixelAt(area.Left + x, area.Top + y));
                }
            }

            return pixels;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestLogger : ILogger<ScreenCaptureService>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
