using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Assistant.Core.Clock;
using Assistant.Core.Displays;
using Assistant.Core.Memory;
using Assistant.Windows.Displays;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Clock;

// The part of the driver that puts the Clock app's window on the display the user chose for it.
public sealed partial class WindowsClockApp
{
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;

    // Where the user's choice of display is kept (Settings, under Memory); without it the window stays where Windows opens it.
    private readonly IMemoryStore? _memory;

    /// <summary>Creates the driver, which puts the Clock app's window on the display the user chose (<see cref="ClockPlace"/>) in <paramref name="memory"/>.</summary>
    /// <param name="memory">What the Assistant remembers for the user.</param>
    /// <param name="logger">Where what was done is noted.</param>
    public WindowsClockApp(IMemoryStore? memory, Microsoft.Extensions.Logging.ILogger<WindowsClockApp>? logger = null)
        : this(logger)
    {
        _memory = memory;
    }

    /// <inheritdoc/>
    public async Task<ClockResult> ApplyPlaceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    try
                    {
                        // Only a window that is there is moved: the Clock app is not opened for this.
                        if (!IsRunning() || (FindWindow() is null && (_frame == 0 || !User32.IsWindow(_frame))))
                        {
                            return ClockResult.Ok(string.Empty);
                        }

                        return PlaceFrame() ? ClockResult.Ok("The Clock window was moved there.") : ClockResult.Ok(string.Empty);
                    }
                    catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or Win32Exception)
                    {
                        return ClockResult.Ok(string.Empty);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ClockSpot?> ReadSpotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    try
                    {
                        // Only a window that is there is looked at: the Clock app is not opened for this.
                        if (!IsRunning() || (FindWindow() is null && (_frame == 0 || !User32.IsWindow(_frame))))
                        {
                            return null;
                        }

                        var frame = _frame;
                        return frame != 0 && User32.IsWindow(frame) && !IsZoomed(frame) && User32.GetWindowRect(frame, out var rect)
                            && WindowsDisplays.Of(frame) is { } on && WindowsDisplays.WorkAreaOf(on) is { } area
                            ? SpotOf(rect, area)
                            : null;
                    }
                    catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or Win32Exception)
                    {
                        return null;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Moves the Clock app's window to where the user chose: to the display they chose, at the same size and at the same place on it (a small timer
    // that sat in the top right corner sits in the top right corner there), and to the place on the display they chose, when they chose one. Says
    // whether the window is there now. A window that is there already is left alone, and so is everything when the user chose nothing.
    private bool PlaceFrame()
    {
        var frame = _frame;
        if (_memory is null || frame == 0 || !User32.IsWindow(frame))
        {
            return false;
        }

        var displays = new WindowsDisplays().List();
        if (ClockPlace.ReadPlacement(_memory, displays) is not { } placement || WindowsDisplays.Of(frame) is not { } now)
        {
            return false;
        }

        var wanted = placement.Display ?? now;
        var same = string.Equals(now.Id, wanted.Id, StringComparison.OrdinalIgnoreCase);
        var filled = IsZoomed(frame);
        if (same && (placement.Spot is null || filled))
        {
            // It is on its display, and has no place of its own there (none was chosen, or it fills the display).
            return true;
        }

        // A window that fills its display is put back to its own size, moved, and made to fill the other.
        if (filled)
        {
            User32.ShowWindow(frame, SW_RESTORE);
        }

        if (!User32.GetWindowRect(frame, out var rect) || WindowsDisplays.WorkAreaOf(now) is not { } from || WindowsDisplays.WorkAreaOf(wanted) is not { } to)
        {
            return false;
        }

        var (x, y) = placement.Spot is { } spot && !filled ? SpotOn(rect, to, spot) : PlaceOn(rect, from, to);
        if (same && Math.Abs(x - rect.Left) <= 1 && Math.Abs(y - rect.Top) <= 1)
        {
            return true;
        }

        var moved = User32.SetWindowPos(frame, 0, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        if (filled)
        {
            User32.ShowWindow(frame, SW_MAXIMIZE);
        }

        if (moved)
        {
            LogDone(_logger, "place", true);
        }

        return moved;
    }

    /// <summary>
    /// Where a window at <paramref name="window"/> on the display whose usable part is <paramref name="from"/> goes on the one whose usable part is
    /// <paramref name="to"/>: as far along the room it has there as it was along the room it had, so that it keeps its corner or its middle, and
    /// always inside the display it is moved to.
    /// </summary>
    internal static (int X, int Y) PlaceOn(User32.Rect window, User32.Rect from, User32.Rect to)
    {
        static int Along(int at, int size, int fromStart, int fromEnd, int toStart, int toEnd)
        {
            var roomBefore = fromEnd - fromStart - size;
            var share = roomBefore > 0 ? Math.Clamp((at - fromStart) / (double)roomBefore, 0, 1) : 0.5;
            var roomAfter = Math.Max(0, toEnd - toStart - size);
            return toStart + (int)Math.Round(share * roomAfter);
        }

        return (
            Along(window.Left, window.Right - window.Left, from.Left, from.Right, to.Left, to.Right),
            Along(window.Top, window.Bottom - window.Top, from.Top, from.Bottom, to.Top, to.Bottom));
    }

    /// <summary>Where a window at <paramref name="window"/> goes on the display whose usable part is <paramref name="to"/> to sit at <paramref name="spot"/>.</summary>
    internal static (int X, int Y) SpotOn(User32.Rect window, User32.Rect to, ClockSpot spot)
    {
        static int Along(double share, int size, int start, int end) => start + (int)Math.Round(Math.Clamp(share, 0, 1) * Math.Max(0, end - start - size));
        return (
            Along(spot.X, window.Right - window.Left, to.Left, to.Right),
            Along(spot.Y, window.Bottom - window.Top, to.Top, to.Bottom));
    }

    /// <summary>Where a window at <paramref name="window"/> is on the display whose usable part is <paramref name="area"/>, and how much of it the window takes.</summary>
    internal static ClockSpot SpotOf(User32.Rect window, User32.Rect area)
    {
        static (double Share, double Size) Along(int at, int size, int start, int end)
        {
            var room = end - start - size;
            var whole = Math.Max(1, end - start);
            return (room > 0 ? Math.Clamp((at - start) / (double)room, 0, 1) : 0.5, Math.Clamp(size / (double)whole, 0, 1));
        }

        var (x, width) = Along(window.Left, window.Right - window.Left, area.Left, area.Right);
        var (y, height) = Along(window.Top, window.Bottom - window.Top, area.Top, area.Bottom);
        return new ClockSpot(x, y, width, height);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);
}
