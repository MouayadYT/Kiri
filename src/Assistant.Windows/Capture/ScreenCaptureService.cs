using System.Diagnostics;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Capture;

/// <summary>
/// The app's <see cref="IScreenCapture"/>: captures a monitor, a window or a rectangle of the screen, locally, into memory (PROJECT_SPEC
/// §4.6). A copy of the screen is made with GDI, which sees the screen as the Desktop Window Manager composes it, so every monitor
/// and every window in front shows; a window is asked to render itself with <c>PrintWindow</c> so that what covers it does not.
/// Each capture is a new <see cref="CapturedImage"/> that nothing else keeps: nothing is written to disk or logged.
/// </summary>
/// <remarks>
/// <para>
/// The calls run on the thread pool, so a capture of several large monitors does not hold up the window that asked, and the
/// monitors of a snapshot are copied at the same time. They need the process to be per-monitor DPI aware (v2), as the application
/// manifest declares, so that rectangles and pixels are the same physical ones on every monitor.
/// </para>
/// <para>
/// Windows does not let anything copy the screen while the PC is locked or a secure desktop shows, and a window whose content is
/// protected (such as some video) comes out black: those captures fail with <see cref="ScreenCaptureFailure.Refused"/> or show what
/// Windows chooses to give.
/// </para>
/// </remarks>
public sealed class ScreenCaptureService : IScreenCapture
{
    /// <summary>The most pixels one capture holds, 64 million (an 8K monitor is 33 million): its pixels take four bytes each.</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    private readonly IScreenCaptureNativeMethods _native;
    private readonly TimeProvider _clock;
    private readonly ILogger<ScreenCaptureService> _logger;

    /// <summary>Creates the service over the real screen.</summary>
    public ScreenCaptureService(ILogger<ScreenCaptureService> logger, TimeProvider clock)
        : this(logger, clock, new ScreenCaptureNativeMethods())
    {
    }

    internal ScreenCaptureService(ILogger<ScreenCaptureService> logger, TimeProvider clock, IScreenCaptureNativeMethods native)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _logger = logger;
        _clock = clock;
        _native = native;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CaptureMonitor> GetMonitors() => _native.Monitors();

    /// <inheritdoc/>
    public Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return RunAsync(CaptureKind.Monitor, () => CaptureMonitor(monitor), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default)
    {
        var monitors = _native.Monitors();
        if (monitors.Count == 0)
        {
            throw Fail(CaptureKind.Monitor, ScreenCaptureFailure.NothingToCapture);
        }

        var captures = monitors.Select(monitor => RunAsync(CaptureKind.Monitor, () => CaptureMonitor(monitor), cancellationToken)).ToArray();
        try
        {
            return await Task.WhenAll(captures).ConfigureAwait(false);
        }
        catch
        {
            // One failing leaves the others' pixels with nobody to wipe them.
            foreach (var capture in captures.Where(capture => capture.IsCompletedSuccessfully))
            {
                capture.Result.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default) =>
        RunAsync(CaptureKind.Window, () => CaptureWindow(window), cancellationToken);

    /// <inheritdoc/>
    public Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default) =>
        RunAsync(CaptureKind.Region, () => CaptureRegion(region), cancellationToken);

    // A capture on a thread-pool thread; a failure is logged once, with what failed and no more.
    private async Task<CapturedImage> RunAsync(CaptureKind kind, Func<CapturedImage> capture, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return capture();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ScreenCaptureException failure)
        {
            CaptureLog.Failed(_logger, kind, failure.Failure, failure.ErrorCode);
            throw;
        }
    }

    internal CapturedImage CaptureMonitor(CaptureMonitor monitor) => CopyArea(CaptureKind.Monitor, monitor.Bounds, monitor);

    internal CapturedImage CaptureRegion(ScreenRect region) => CopyArea(CaptureKind.Region, region, null);

    internal CapturedImage CaptureWindow(nint window)
    {
        var start = Stopwatch.GetTimestamp();
        if (_native.DescribeWindow(window) is not { } frame || frame.Visible.Width <= 0 || frame.Visible.Height <= 0
            || frame.Outer.Width <= 0 || frame.Outer.Height <= 0)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.WindowUnavailable);
        }

        if ((long)frame.Outer.Width * frame.Outer.Height > MaxPixels)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.TooLarge);
        }

        var monitors = _native.Monitors();
        var error = _native.RenderWindow(window, frame.Outer.Width, frame.Outer.Height, out var rendered);
        if (error == 0 && rendered is not null)
        {
            // The window rendered all of itself, from its outer corner; what is seen is the part inside its invisible border.
            var seen = Intersect(frame.Visible, frame.Outer);
            if (seen.Width > 0 && seen.Height > 0)
            {
                var image = new CapturedImage(
                    CaptureKind.Window, CaptureMethod.WindowRender, seen, LargestShare(seen, monitors),
                    CapturedImage.CopyPart(rendered, frame.Outer, seen), _clock.GetUtcNow());
                Array.Clear(rendered);
                Log(image, start);
                return image;
            }

            Array.Clear(rendered);
        }
        else
        {
            CaptureLog.WindowFellBack(_logger, error);
        }

        // The window would not draw itself (some windows do not): what is on screen where it is, covered or not, is the next best.
        var fallback = CopyArea(CaptureKind.Window, frame.Visible, null);
        return fallback;
    }

    // Copies a rectangle of the screen, the part of it that is on the virtual screen.
    private CapturedImage CopyArea(CaptureKind kind, ScreenRect area, CaptureMonitor? monitor)
    {
        var start = Stopwatch.GetTimestamp();
        var monitors = _native.Monitors();
        var screen = VirtualScreen(monitors);
        var part = screen is { } virtualScreen ? Intersect(area, virtualScreen) : default;
        if (area.Width <= 0 || area.Height <= 0 || part.Width <= 0 || part.Height <= 0)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.NothingToCapture);
        }

        if ((long)part.Width * part.Height > MaxPixels)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.TooLarge);
        }

        var error = _native.CopyScreen(part, out var pixels);
        if (error != 0 || pixels is null)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.Refused, error);
        }

        var image = new CapturedImage(
            kind, CaptureMethod.ScreenCopy, part, monitor ?? LargestShare(part, monitors), pixels, _clock.GetUtcNow());
        Log(image, start);
        return image;
    }

    private void Log(CapturedImage image, long start) =>
        CaptureLog.Captured(_logger, image.Kind, image.Method, image.Width, image.Height, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);

    private ScreenCaptureException Fail(CaptureKind kind, ScreenCaptureFailure failure)
    {
        CaptureLog.Failed(_logger, kind, failure, 0);
        return new ScreenCaptureException(failure);
    }

    /// <summary>The smallest rectangle that holds every monitor, or <see langword="null"/> when there is none.</summary>
    internal static ScreenRect? VirtualScreen(IReadOnlyList<CaptureMonitor> monitors) =>
        monitors.Count == 0
            ? null
            : new ScreenRect(
                monitors.Min(monitor => monitor.Bounds.Left), monitors.Min(monitor => monitor.Bounds.Top),
                monitors.Max(monitor => monitor.Bounds.Right), monitors.Max(monitor => monitor.Bounds.Bottom));

    /// <summary>The part of <paramref name="first"/> that is inside <paramref name="second"/>; it has no width or height when there is none.</summary>
    internal static ScreenRect Intersect(ScreenRect first, ScreenRect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top ? new ScreenRect(left, top, right, bottom) : new ScreenRect(left, top, left, top);
    }

    /// <summary>The monitor that holds the most of <paramref name="area"/>, or <see langword="null"/> when it holds none.</summary>
    internal static CaptureMonitor? LargestShare(ScreenRect area, IReadOnlyList<CaptureMonitor> monitors) =>
        monitors
            .Select(monitor => (Monitor: monitor, Share: Intersect(area, monitor.Bounds)))
            .Where(candidate => candidate.Share.Width > 0 && candidate.Share.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Share.Width * candidate.Share.Height)
            .Select(candidate => candidate.Monitor)
            .FirstOrDefault();
}
