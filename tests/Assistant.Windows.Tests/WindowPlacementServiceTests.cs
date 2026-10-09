using System.Runtime.InteropServices;
using Assistant.Windows.Interop;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class WindowPlacementServiceTests
{
    private const nint Overlay = 42;
    private const nint App = 7;
    private static readonly OverlayLayout Bar = new(610, 183, 45, 28, 520, 91, 0.22);

    // Three of the layouts the service must handle: a 125 % primary, a 100 % portrait monitor to its left at negative
    // coordinates, and a 125 % monitor above it.
    private static readonly DisplayMonitor Primary = new(
        new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1380), 120);
    private static readonly DisplayMonitor Portrait = new(
        new ScreenRect(-1080, -491, 0, 1429), new ScreenRect(-1080, -491, 0, 1381), 96);
    private static readonly DisplayMonitor Above = new(
        new ScreenRect(2807, -1200, 4727, 0), new ScreenRect(2807, -1200, 4727, -60), 120);

    [Fact]
    public void InvocationUsesPointerMonitorEvenWhenForegroundIsOnAnotherMonitor()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Portrait, CursorMonitor = Above };
        var service = new WindowPlacementService(new TestLogger(), native);
        var placement = service.PlaceOnCursorMonitor(Overlay, Bar);
        Assert.Same(Above, placement?.Monitor);
        Assert.Equal(MonitorSource.Cursor, placement?.Source);
        Assert.Equal(new ScreenRect(3386, -984, 4149, -755), placement?.Bounds);
        var movedPointer = new FakeNative { Foreground = App, ForegroundMonitor = Portrait, CursorMonitor = Primary };
        Assert.Same(Primary, new WindowPlacementService(new TestLogger(), movedPointer).PlaceOnCursorMonitor(Overlay, Bar)?.Monitor);
    }

    [Fact]
    public void PlacesOnMonitorHoldingForegroundWindowAtItsDpi()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Portrait, CursorMonitor = Above };
        var logger = new TestLogger();

        var placement = new WindowPlacementService(logger, native).PlaceOnActiveMonitor(Overlay, Bar);

        Assert.NotNull(placement);
        Assert.Same(Portrait, placement.Monitor);
        Assert.Equal(MonitorSource.ForegroundWindow, placement.Source);
        Assert.Equal(new ScreenRect(-845, -107, -235, 76), placement.Bounds);
        Assert.Equal([(Overlay, -845, -107)], native.Moves);
        Assert.Contains(logger.Messages, line => line.Contains("ForegroundWindow") && line.Contains("96 DPI"));
    }

    [Fact]
    public void SameForegroundMonitorGivesSamePlacementEveryTime()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Above };
        var service = new WindowPlacementService(new TestLogger(), native);

        var first = service.PlaceOnActiveMonitor(Overlay, Bar);
        var second = service.PlaceOnActiveMonitor(Overlay, Bar);

        Assert.Equal(new ScreenRect(3386, -984, 4149, -755), first?.Bounds);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(false, false, false, true)]  // No foreground window.
    [InlineData(true, true, false, true)]    // The overlay itself, or a window that belongs to it.
    [InlineData(true, false, true, true)]    // The desktop, which spans every monitor.
    [InlineData(true, false, false, false)]  // A window that is on no monitor.
    public void FallsBackToMonitorUnderPointerWithoutUsableForegroundWindow(
        bool hasForeground, bool related, bool desktop, bool foregroundOnMonitor)
    {
        var native = new FakeNative
        {
            Foreground = hasForeground ? App : 0,
            ForegroundRelated = related,
            ForegroundIsDesktop = desktop,
            ForegroundMonitor = foregroundOnMonitor ? Primary : null,
            CursorMonitor = Above,
        };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceOnActiveMonitor(Overlay, Bar);

        Assert.Same(Above, placement?.Monitor);
        Assert.Equal(MonitorSource.Cursor, placement?.Source);
    }

    [Fact]
    public void FallsBackToPrimaryMonitorWithoutPointer()
    {
        var native = new FakeNative { PrimaryMonitorValue = Primary };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceOnActiveMonitor(Overlay, Bar);

        Assert.Same(Primary, placement?.Monitor);
        Assert.Equal(MonitorSource.PrimaryMonitor, placement?.Source);
        Assert.Equal(new ScreenRect(899, 269, 1662, 498), placement?.Bounds);
    }

    [Fact]
    public void LeavesWindowAloneWhenNoMonitorIsFound()
    {
        var native = new FakeNative { PrimaryMonitorValue = null };
        var logger = new TestLogger();

        Assert.Null(new WindowPlacementService(logger, native).PlaceOnActiveMonitor(Overlay, Bar));
        Assert.Empty(native.Moves);
        Assert.Contains(logger.Messages, line => line.Contains("No monitor was found"));
    }

    [Fact]
    public void MovesAgainWhenRescalingForNewDpiMovedTheWindow()
    {
        // A window that rescales itself for a new DPI may take its suggested position, rather than the requested one.
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Portrait, DriftOnFirstMove = (-900, -150) };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceOnActiveMonitor(Overlay, Bar);

        Assert.Equal([(Overlay, -845, -107), (Overlay, -845, -107)], native.Moves);
        Assert.Equal((-845, -107), native.Origin);
        Assert.NotNull(placement);
    }

    [Fact]
    public void MoveFailureIsLoggedAndReported()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Primary, MoveError = 1400 };
        var logger = new TestLogger();

        Assert.Null(new WindowPlacementService(logger, native).PlaceOnActiveMonitor(Overlay, Bar));
        Assert.Single(native.Moves);
        Assert.Contains(logger.Messages, line => line.Contains("1400"));
    }

    [Fact]
    public void PlacesAtAPointOnTheNearestMonitorAndReadsItBack()
    {
        var native = new FakeNative { NearestMonitor = Portrait };
        var service = new WindowPlacementService(new TestLogger(), native);

        var placement = service.PlaceAnchorTopAt(Overlay, Bar, new ScreenPoint(-540, 100));

        Assert.NotNull(placement);
        Assert.Equal((Portrait, MonitorSource.Point), (placement.Monitor, placement.Source));
        Assert.Equal(new ScreenRect(-845, 72, -235, 255), placement.Bounds);
        Assert.Equal((-540, 100), native.NearestQueries.Single());
        Assert.Equal(new ScreenPoint(-540, 100), service.GetAnchorTop(Overlay, Bar));
    }

    [Fact]
    public void PointPlacementWithoutMonitorLeavesWindowAlone()
    {
        var native = new FakeNative { NearestMonitor = null };
        Assert.Null(new WindowPlacementService(new TestLogger(), native).PlaceAnchorTopAt(Overlay, Bar, new ScreenPoint(5, 5)));
        Assert.Empty(native.Moves);
    }

    [Fact]
    public void FitAnchorTopReportsWhereAPlacementWouldPutTheAnchorWithoutMovingAnything()
    {
        var native = new FakeNative { NearestMonitor = Portrait };
        var service = new WindowPlacementService(new TestLogger(), native);
        var tall = new OverlayLayout(610, 690, 96, 28, 418, 598, 0.22);

        // Room for the layout: the anchor stays where it was asked for.
        Assert.Equal(new ScreenPoint(-540, 100), service.FitAnchorTop(Bar, new ScreenPoint(-540, 100)));
        Assert.Equal(new ScreenPoint(-540, 100), service.FitAnchorTop(tall, new ScreenPoint(-540, 100)));

        // No room below: the taller layout's anchor is drawn up until all of it is on screen, while the bar's still fits.
        Assert.Equal(new ScreenPoint(-540, 783), service.FitAnchorTop(tall, new ScreenPoint(-540, 1200)));
        Assert.Equal(new ScreenPoint(-540, 1200), service.FitAnchorTop(Bar, new ScreenPoint(-540, 1200)));
        Assert.Equal(new ScreenPoint(-540, 100), service.FitAnchorTop(Bar, new ScreenPoint(-540, 100)));
        Assert.Empty(native.Moves);

        // It agrees with a real placement.
        var placement = service.PlaceAnchorTopAt(Overlay, tall, new ScreenPoint(-540, 1200));
        Assert.Equal(service.FitAnchorTop(tall, new ScreenPoint(-540, 1200)), service.GetAnchorTop(Overlay, tall));
        Assert.Equal(783 - 28, placement?.Bounds.Top);
    }

    [Fact]
    public void FitAnchorTopWithoutMonitorReportsNothing()
    {
        var service = new WindowPlacementService(new TestLogger(), new FakeNative { NearestMonitor = null });

        Assert.Null(service.FitAnchorTop(Bar, new ScreenPoint(5, 5)));
    }

    [Fact]
    public void RejectsMissingWindow()
    {
        var service = new WindowPlacementService(new TestLogger(), new FakeNative());

        Assert.Throws<ArgumentOutOfRangeException>(() => service.PlaceOnActiveMonitor(0, Bar));
    }

    [Fact]
    public void NativePlacementMovesRealWindowOntoForegroundMonitor()
    {
        // A hidden window, so nothing appears on screen.
        var window = User32.CreateWindow(0, "STATIC", null, User32.WS_POPUP, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var foreground = User32.GetForegroundWindow();
            var placement = new WindowPlacementService(new TestLogger()).PlaceOnActiveMonitor(window, Bar);

            Assert.NotNull(placement);
            Assert.True(User32.GetWindowRect(window, out var rect));
            Assert.Equal((placement.Bounds.Left, placement.Bounds.Top), (rect.Left, rect.Top));
            Assert.NotEqual(0, User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONULL));
            var work = placement.Monitor.WorkArea;
            Assert.InRange(placement.Bounds.Left, work.Left, work.Right - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Top, work.Top, work.Bottom - placement.Bounds.Height);

            // Unless the user switched windows meanwhile, an application in the foreground decides the monitor.
            if (placement.Source == MonitorSource.ForegroundWindow && User32.GetForegroundWindow() == foreground)
            {
                var info = new User32.MonitorInfo { Size = (uint)Marshal.SizeOf<User32.MonitorInfo>() };
                Assert.True(User32.GetMonitorInfo(User32.MonitorFromWindow(foreground, User32.MONITOR_DEFAULTTONULL), ref info));
                Assert.Equal(new ScreenRect(info.WorkArea.Left, info.WorkArea.Top, info.WorkArea.Right, info.WorkArea.Bottom), work);
            }
        }
        finally
        {
            User32.DestroyWindow(window);
        }
    }

    [Fact]
    public void CentersAWindowOfPreferredSizeOnTheActiveMonitorAtItsDpi()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Primary };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceCenteredOnActiveMonitor(Overlay, 1440, 824, 24);

        // 1800 × 1030 pixels at 125 %, centered in the work area above the taskbar.
        Assert.Equal(new ScreenRect(380, 175, 2180, 1205), placement?.Bounds);
        Assert.Equal([(Overlay, new ScreenRect(380, 175, 2180, 1205))], native.BoundsSet);
    }

    [Fact]
    public void ShrinksAWindowTooBigForTheWorkAreaToLeaveTheMargin()
    {
        var native = new FakeNative { Foreground = App, ForegroundMonitor = Portrait };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceCenteredOnActiveMonitor(Overlay, 1440, 824, 24);

        // The 1080-pixel-wide portrait monitor at 100 % leaves 1032 pixels between 24-pixel margins.
        Assert.Equal(new ScreenRect(-1056, 33, -24, 857), placement?.Bounds);
    }

    [Fact]
    public void SetsTheBoundsAgainWhenRescalingForNewDpiChangedThem()
    {
        var native = new FakeNative
        {
            Foreground = App, ForegroundMonitor = Portrait, DriftOnFirstBounds = new ScreenRect(-1100, 0, -30, 900),
        };

        var placement = new WindowPlacementService(new TestLogger(), native).PlaceCenteredOnActiveMonitor(Overlay, 800, 600, 24);

        var expected = new ScreenRect(-940, 145, -140, 745);
        Assert.Equal(expected, placement?.Bounds);
        Assert.Equal([(Overlay, expected), (Overlay, expected)], native.BoundsSet);
    }

    // ---- Beside another application's window ------------------------------------------------------------------------------------

    private static readonly OverlayLayout Panel = new(508, 690, 45, 28, 418, 598, 0.22);
    private static readonly ScreenRect BrowserBounds = new(0, 0, 2560, 1380);

    [Fact]
    public void NotesTheForegroundWindowWhereItIsAndWhereThePointerIs()
    {
        var native = new FakeNative
        {
            Foreground = App, ProcessIdOf = 1234, VisibleBounds = BrowserBounds, PointerAt = new ScreenPoint(900, 500),
        };

        var target = new WindowPlacementService(new TestLogger(), native).DescribeForegroundWindow(excludeProcessId: 99);

        Assert.Equal(new NearWindowTarget(App, 1234, BrowserBounds, new ScreenPoint(900, 500)), target);
    }

    [Theory]
    [InlineData(false, false, true, 1234, true)]  // Nothing in front.
    [InlineData(true, true, true, 1234, true)]    // The desktop, which is not an application's window.
    [InlineData(true, false, false, 1234, true)]  // Minimized or hidden.
    [InlineData(true, false, true, 99, true)]     // The overlay's own application.
    [InlineData(true, false, true, 0, true)]      // A window that is gone.
    [InlineData(true, false, true, 1234, false)]  // Windows would not say where it is.
    public void NotesNoWindowWhenThereIsNoneToOpenBeside(
        bool hasForeground, bool desktop, bool onScreen, int processId, bool hasBounds)
    {
        var native = new FakeNative
        {
            Foreground = hasForeground ? App : 0, ForegroundIsDesktop = desktop, OnScreen = onScreen, ProcessIdOf = processId,
            VisibleBounds = hasBounds ? BrowserBounds : null,
        };

        Assert.Null(new WindowPlacementService(new TestLogger(), native).DescribeForegroundWindow(excludeProcessId: 99));
    }

    [Fact]
    public void FindsThePointBesideTheWindowOnTheMonitorHoldingItsMiddle()
    {
        var native = new FakeNative { NearestMonitor = Primary };
        var service = new WindowPlacementService(new TestLogger(), native);
        var target = new NearWindowTarget(App, 1234, new ScreenRect(400, 200, 1800, 1100), null);

        var point = service.FindAnchorTopNear(target, Panel);

        // The monitor is asked for at the window's middle, and the panel is as for 125 %: 522.5 wide, against the right edge.
        Assert.Equal([(1100, 650)], native.NearestQueries);
        Assert.Equal(new ScreenPoint(1509, 320), point);
        Assert.Empty(native.Moves);
    }

    [Fact]
    public void FindsNoPointForAWindowOnNoMonitor_AndSaysSo()
    {
        var native = new FakeNative { NearestMonitor = null };
        var logger = new TestLogger();

        Assert.Null(new WindowPlacementService(logger, native).FindAnchorTopNear(
            new NearWindowTarget(App, 1234, new ScreenRect(400, 200, 1800, 1100), null), Panel));
        Assert.Contains(logger.Messages, line => line.Contains("No monitor was found"));
    }

    private sealed class FakeNative : IPlacementNativeMethods
    {
        public int ProcessIdOf { get; init; } = 100;
        public bool OnScreen { get; init; } = true;
        public ScreenRect? VisibleBounds { get; init; }
        public ScreenPoint? PointerAt { get; init; }

        public int GetProcessId(nint window) => window == Foreground && window != 0 ? ProcessIdOf : 0;
        public bool IsOnScreen(nint window) => window == Foreground && OnScreen;
        public ScreenRect? GetVisibleBounds(nint window) => window == Foreground ? VisibleBounds : null;
        public ScreenPoint? GetPointer() => PointerAt;

        public nint Foreground { get; init; }
        public bool ForegroundRelated { get; init; }
        public bool ForegroundIsDesktop { get; init; }
        public DisplayMonitor? ForegroundMonitor { get; init; }
        public DisplayMonitor? CursorMonitor { get; init; }
        public DisplayMonitor? PrimaryMonitorValue { get; init; } = Primary;
        public int MoveError { get; init; }
        public (int X, int Y)? DriftOnFirstMove { get; init; }
        public List<(nint Window, int X, int Y)> Moves { get; } = [];
        public (int X, int Y) Origin { get; private set; }

        public nint GetForegroundWindow() => Foreground;
        public bool AreRelated(nint window, nint other) => window == Foreground && other == Overlay && ForegroundRelated;
        public bool IsDesktop(nint window) => window == Foreground && ForegroundIsDesktop;
        public DisplayMonitor? MonitorFromWindow(nint window) => window == Foreground ? ForegroundMonitor : null;
        public DisplayMonitor? MonitorFromCursor() => CursorMonitor;
        public DisplayMonitor? PrimaryMonitor() => PrimaryMonitorValue;
        public DisplayMonitor? NearestMonitor { get; init; } = Primary;
        public List<(int X, int Y)> NearestQueries { get; } = [];

        public DisplayMonitor? MonitorNearest(int x, int y)
        {
            NearestQueries.Add((x, y));
            return NearestMonitor;
        }

        public int Move(nint window, int x, int y)
        {
            Moves.Add((window, x, y));
            Origin = Moves.Count == 1 && DriftOnFirstMove is { } drift ? drift : (x, y);
            return MoveError;
        }

        public (int X, int Y)? GetOrigin(nint window) => Origin;

        public List<(nint Window, ScreenRect Bounds)> BoundsSet { get; } = [];
        public ScreenRect? DriftOnFirstBounds { get; init; }
        public ScreenRect? Bounds { get; private set; }

        public int SetBounds(nint window, ScreenRect bounds)
        {
            BoundsSet.Add((window, bounds));
            Bounds = BoundsSet.Count == 1 && DriftOnFirstBounds is { } drift ? drift : bounds;
            return MoveError;
        }

        public ScreenRect? GetBounds(nint window) => Bounds;
    }

    private sealed class TestLogger : ILogger<WindowPlacementService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
