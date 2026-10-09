using System.Runtime.InteropServices;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Capture on the real screen, whatever monitors this PC has: the layout Windows reports, a picture of each monitor at its own
/// size, and a region that lands on the same pixels whichever monitor it is on, left of or above the primary included. The tests
/// look at sizes and at whether pictures agree, never at what is on screen, and keep nothing. They are skipped while the PC is locked,
/// as Windows lets nothing copy the screen then (<see cref="DesktopFactAttribute"/>).
/// </summary>
public sealed class RealScreenCaptureTests
{
    // What the application manifest declares for the real app: physical pixels on every monitor.
    private static readonly nint PerMonitorV2 = -4;

    private static readonly ScreenCaptureService Service = new(NullLogger<ScreenCaptureService>.Instance, TimeProvider.System);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DesktopFact]
    public void TheRealMonitorsAreListedLeftToRight_OneIsPrimary_AndEachHasItsDpi()
    {
        OnTheDesktop(() =>
        {
            var monitors = Service.GetMonitors();

            Assert.NotEmpty(monitors);
            Assert.Equal(1, monitors.Count(monitor => monitor.IsPrimary));
            Assert.Equal(Enumerable.Range(0, monitors.Count), monitors.Select(monitor => monitor.Index));
            Assert.Equal(monitors.OrderBy(monitor => monitor.Bounds.Left).ThenBy(monitor => monitor.Bounds.Top), monitors);
            Assert.All(monitors, monitor =>
            {
                Assert.True(monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0);
                Assert.InRange(monitor.Dpi, 96, 480);
            });
        });
    }

    [DesktopFact]
    public void EachMonitorIsCapturedAtItsOwnSize_OpaqueAndNotBlank()
    {
        OnTheDesktop(() =>
        {
            foreach (var monitor in Service.GetMonitors())
            {
                using var image = Service.CaptureMonitor(monitor);

                Assert.Equal(monitor.Bounds, image.Bounds);
                Assert.Equal((monitor.Bounds.Width, monitor.Bounds.Height), (image.Width, image.Height));
                Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
                Assert.Same(monitor, image.Monitor);
                Assert.InRange(image.CapturedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

                // Every pixel is opaque, and a desktop is not one flat color.
                var span = image.Pixels.Span;
                var colors = new HashSet<uint>();
                for (var index = 0; index < span.Length; index += 4 * 997)
                {
                    Assert.Equal(255, span[index + 3]);
                    colors.Add(BitConverter.ToUInt32(span[index..]));
                }

                Assert.True(colors.Count > 3, "The monitor's capture is blank.");
            }
        });
    }

    [DesktopFact]
    public void ARegionCaptureLandsOnTheSamePixelsAsTheMonitorCaptureAtThatPlace_OnEveryMonitor()
    {
        OnTheDesktop(() =>
        {
            foreach (var monitor in Service.GetMonitors())
            {
                // A little in from the corner, so a wrong origin (an unscaled offset, a missing negative one) would show.
                var region = new ScreenRect(
                    monitor.Bounds.Left + 137, monitor.Bounds.Top + 91, monitor.Bounds.Left + 137 + 301, monitor.Bounds.Top + 91 + 173);
                using var snapshot = Service.CaptureMonitor(monitor);
                using var crop = snapshot.Crop(region);
                using var direct = Service.CaptureRegion(region);

                Assert.Equal(region, direct.Bounds);
                Assert.Equal(region, crop.Bounds);
                Assert.Equal(monitor, direct.Monitor);
                Assert.True(
                    Agreement(crop, direct) >= 0.9,
                    $"Monitor {monitor.Index} at {monitor.Dpi} DPI: the region and the snapshot disagree.");
            }
        });
    }

    [DesktopFact]
    public void ARegionAcrossTwoMonitorsThatTouch_IsOnePicture_ThatAgreesWithBoth()
    {
        OnTheDesktop(() =>
        {
            var monitors = Service.GetMonitors();
            foreach (var left in monitors)
            {
                var right = monitors.FirstOrDefault(other =>
                    other.Bounds.Left == left.Bounds.Right && other.Bounds.Top < left.Bounds.Bottom - 300 && other.Bounds.Bottom > left.Bounds.Top + 300);
                if (right is null)
                {
                    continue;
                }

                var top = Math.Max(left.Bounds.Top, right.Bounds.Top) + 100;
                var region = new ScreenRect(left.Bounds.Right - 120, top, left.Bounds.Right + 130, top + 150);
                using var picture = Service.CaptureRegion(region);
                using var leftSnapshot = Service.CaptureMonitor(left);
                using var rightSnapshot = Service.CaptureMonitor(right);
                using var leftPart = leftSnapshot.Crop(new ScreenRect(region.Left, region.Top, left.Bounds.Right, region.Bottom));
                using var rightPart = rightSnapshot.Crop(new ScreenRect(right.Bounds.Left, region.Top, region.Right, region.Bottom));
                using var leftHalf = picture.Crop(leftPart.Bounds);
                using var rightHalf = picture.Crop(rightPart.Bounds);

                Assert.Equal((250, 150), (picture.Width, picture.Height));
                Assert.True(Agreement(leftHalf, leftPart) >= 0.9);
                Assert.True(Agreement(rightHalf, rightPart) >= 0.9);
            }
        });
    }

    [DesktopFact]
    public void TheWholeVirtualScreen_IsCapturedInOnePicture_AsWideAsAllTheMonitors()
    {
        OnTheDesktop(() =>
        {
            var monitors = Service.GetMonitors();
            var all = ScreenCaptureService.VirtualScreen(monitors)!.Value;
            if ((long)all.Width * all.Height > ScreenCaptureService.MaxPixels)
            {
                return;
            }

            using var picture = Service.CaptureRegion(all);

            Assert.Equal(all, picture.Bounds);
            Assert.Equal(all.Width * all.Height * 4, picture.Pixels.Length);
        });
    }

    [DesktopFact]
    public void AWindowIsCapturedAsItIsSeen_TheTaskbarForOne()
    {
        OnTheDesktop(() =>
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == 0)
            {
                return;
            }

            using var picture = Service.CaptureWindow(taskbar);

            Assert.Equal(CaptureKind.Window, picture.Kind);
            Assert.True(picture.Width > 100 && picture.Height > 10);
            Assert.NotNull(picture.Monitor);
            Assert.Equal(picture.Width * picture.Height * 4, picture.Pixels.Length);
        });
    }

    [DesktopFact]
    public void AWindowThatDoesNotExist_IsRefused()
    {
        OnTheDesktop(() =>
        {
            var failure = Assert.Throws<ScreenCaptureException>(() => Service.CaptureWindow(0x7FFFFF0));

            Assert.Equal(ScreenCaptureFailure.WindowUnavailable, failure.Failure);
        });
    }

    // The share of the pixels two pictures of the same size have in common (the screen may change a little between captures).
    private static double Agreement(CapturedImage first, CapturedImage second)
    {
        Assert.Equal((first.Width, first.Height), (second.Width, second.Height));
        var a = first.Pixels.Span;
        var b = second.Pixels.Span;
        var same = 0;
        for (var index = 0; index < a.Length; index += 4)
        {
            same += BitConverter.ToUInt32(a[index..]) == BitConverter.ToUInt32(b[index..]) ? 1 : 0;
        }

        return (double)same / (a.Length / 4);
    }

    // Runs the test with physical pixels on this thread, as in the app.
    private static void OnTheDesktop(Action test)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorV2);
        try
        {
            test();
        }
        finally
        {
            if (previous != 0)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }
}

/// <summary>A test that needs the interactive desktop: it is skipped while the PC is locked, when no window is in the foreground.</summary>
internal sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (GetForegroundWindow() == 0)
        {
            Skip = "The PC is locked, so Windows lets nothing copy the screen.";
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
