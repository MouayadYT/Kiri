using System.Windows;
using System.Windows.Threading;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;

namespace Assistant.UI.Capture;

/// <summary>
/// The Visual Intelligence overlay (PROJECT_SPEC §4.6) over every monitor at once: one <see cref="CaptureOverlayWindow"/> on each,
/// showing that monitor's snapshot dimmed. The user draws a selection on any one of them; starting another, on this monitor or
/// another, takes the first away. It ends when the user chooses an action for a selection, or gives up (Esc, the right button, or
/// by leaving the overlay for another window), and wipes the snapshots whichever it was.
/// </summary>
internal sealed class CaptureOverlay : ICaptureOverlay
{
    private readonly Action<CaptureOverlayWindow> _cover;

    /// <summary>Creates the overlay, which puts each window over its monitor.</summary>
    public CaptureOverlay()
        : this(window => window.CoverMonitor())
    {
    }

    // A test puts the windows somewhere that is not over the monitors.
    internal CaptureOverlay(Action<CaptureOverlayWindow> cover) => _cover = cover;

    /// <inheritdoc/>
    /// <remarks>Call it on the UI thread: it creates and shows windows.</remarks>
    public Task<CaptureOutcome?> SelectAsync(
        IReadOnlyList<CapturedImage> snapshots, ChipAvailability imageSearch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(imageSearch);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var completion = new TaskCompletionSource<CaptureOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<CaptureOverlayWindow>(snapshots.Count);
        var finished = false;

        void Finish(CaptureOutcome? outcome)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            foreach (var window in windows)
            {
                window.ReleasePicture();
                window.Close();
            }

            // Whatever the outcome, the snapshots of the whole screen are not kept: the part selected has been copied out of them.
            foreach (var snapshot in snapshots)
            {
                snapshot.Dispose();
            }

            completion.TrySetResult(outcome);
        }

        try
        {
            foreach (var snapshot in snapshots)
            {
                var window = new CaptureOverlayWindow(snapshot, imageSearch);
                window.SelectionStarted += (sender, _) =>
                {
                    foreach (var other in windows.Where(other => !ReferenceEquals(other, sender)))
                    {
                        other.ClearSelection();
                    }
                };
                window.ActionRequested += (_, request) =>
                {
                    if (!finished && window.CropSelection() is { } region)
                    {
                        Finish(new CaptureOutcome(request.Action, region, request.Question));
                    }
                };
                window.CancelRequested += (_, _) => Finish(null);

                // Leaving for another window (the Windows key, Alt+Tab) gives up, so a topmost overlay never traps the user. A move from
                // one overlay window to another, as when the pointer crosses to another monitor, does not.
                window.DeactivatedWindow += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (!finished && !windows.Any(other => other.IsActive))
                    {
                        Finish(null);
                    }
                });
                windows.Add(window);
            }

            var focus = WindowUnderThePointer(windows);
            foreach (var window in windows)
            {
                window.ShowActivated = ReferenceEquals(window, focus);
                _cover(window);
                window.Show();
            }

            if (focus is { ShowActivated: true })
            {
                focus.TakeFocus();
            }
        }
        catch
        {
            Finish(null);
            throw;
        }

        cancellationToken.Register(() => dispatcher.BeginInvoke(() => Finish(null)));
        return completion.Task;
    }

    // The overlay of the monitor the pointer is on, which takes the keyboard; the first when the pointer's place is not known.
    private static CaptureOverlayWindow? WindowUnderThePointer(IReadOnlyList<CaptureOverlayWindow> windows)
    {
        if (ScreenOverlayWindows.CursorPosition() is { } pointer)
        {
            var under = windows.FirstOrDefault(window =>
            {
                var bounds = window.Snapshot.Bounds;
                return pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
            });
            if (under is not null)
            {
                return under;
            }
        }

        return windows.Count > 0 ? windows[0] : null;
    }
}
