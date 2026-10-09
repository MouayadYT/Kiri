using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Capture;

/// <summary>
/// Visual Intelligence (PROJECT_SPEC §4.6): what happens when its shortcut is pressed. The Assistant's own window goes, a snapshot is
/// taken of every monitor, the overlay shows it dimmed while the user selects a part of one, and the chips beside the selection say
/// what to do with it: Ask Assistant opens the floating conversation with the part attached and the composer waiting (or asks what was
/// typed in the chip at once), Copy puts the picture on the clipboard, and Image Search, which sends it out, waits until it can be used.
/// </summary>
/// <remarks>
/// <para>
/// It only starts when the user asks, and only while the Screen Capture permission is on. Everything captured stays in memory: the
/// snapshots are wiped when the overlay closes, and the selected part is a copy that belongs to the conversation it is attached to
/// until the user takes it off (<see cref="ScreenAttachments"/>). Nothing is written to disk or logged.
/// </para>
/// <para>Pressing the shortcut again while the overlay is up gives up, like Esc.</para>
/// </remarks>
internal sealed class VisualIntelligenceController
{
    /// <summary>The longest picture side a conversation's chip needs a thumbnail for.</summary>
    internal const int ThumbnailSide = 128;

    /// <summary>What the chip and the tile call a captured part of the screen.</summary>
    internal const string CaptureName = "Screenshot";

    /// <summary>How long Windows is given to take the Assistant's window off the screen before the snapshot (a few frames).</summary>
    internal static readonly TimeSpan HideSettle = TimeSpan.FromMilliseconds(50);

    /// <summary>What the user is told when Screen Capture is turned off in the Settings.</summary>
    internal const string TurnedOffText =
        "Screen Capture is turned off in Settings, under Permissions, so nothing was captured. Turn Screen Capture on there, then use the shortcut again.";

    /// <summary>What the user is told when Windows would not let the screen be captured.</summary>
    internal const string CaptureFailedText =
        "The screen couldn't be captured. Windows doesn't allow it while the PC is locked or a secure prompt is showing.";

    /// <summary>What the user is told when the picture could not be put on the clipboard.</summary>
    internal const string ClipboardBusyText = "The picture couldn't be copied: another program is using the clipboard. Try again.";

    private readonly IScreenCapture _capture;
    private readonly IPermissionGate _gate;
    private readonly ICaptureOverlay _overlay;
    private readonly IAssistantWindow _assistant;
    private readonly AssistantWindowStateController _windows;
    private readonly ConversationViewModel _conversation;
    private readonly IImageClipboard _clipboard;
    private readonly Func<CancellationToken, Task<ChipAvailability>> _imageSearch;
    private readonly Func<CapturedImage, Task>? _searchImage;
    private readonly ILogger<VisualIntelligenceController> _logger;
    private CancellationTokenSource? _running;

    /// <summary>Creates the controller.</summary>
    /// <param name="capture">Takes the snapshot of the monitors.</param>
    /// <param name="permissions">Says whether Screen Capture is allowed.</param>
    /// <param name="overlay">Shows the snapshot and lets the user select from it.</param>
    /// <param name="assistant">The Assistant's window, which goes while the screen is captured.</param>
    /// <param name="windows">Opens the floating conversation.</param>
    /// <param name="conversation">The floating conversation that the part of the screen is attached to.</param>
    /// <param name="clipboard">Where Copy puts the picture.</param>
    /// <param name="imageSearch">Says whether Image Search can be used now, for the settings as they are saved, and why not if it cannot.</param>
    /// <param name="logger">Receives what happened, never what was seen.</param>
    /// <param name="searchImage">What Image Search does with the selected part; none until it can be used.</param>
    /// <param name="gate">Asks the user before the screen is captured when Screen Capture is set to ask every time (step 119); without it, <paramref name="permissions"/> decides alone and such a capture is refused.</param>
    public VisualIntelligenceController(
        IScreenCapture capture,
        IPermissionPolicy permissions,
        ICaptureOverlay overlay,
        IAssistantWindow assistant,
        AssistantWindowStateController windows,
        ConversationViewModel conversation,
        IImageClipboard clipboard,
        Func<CancellationToken, Task<ChipAvailability>> imageSearch,
        ILogger<VisualIntelligenceController> logger,
        Func<CapturedImage, Task>? searchImage = null,
        IPermissionGate? gate = null)
    {
        _capture = capture;
        _gate = gate ?? new PermissionGate(permissions, null, NullLogger<PermissionGate>.Instance);
        _overlay = overlay;
        _assistant = assistant;
        _windows = windows;
        _conversation = conversation;
        _clipboard = clipboard;
        _imageSearch = imageSearch;
        _searchImage = searchImage;
        _logger = logger;
    }

    /// <summary>Whether the overlay is up.</summary>
    public bool IsActive => _running is not null;

    /// <summary>The shortcut, or a command: starts Visual Intelligence, or, while it is up, gives it up.</summary>
    public async Task InvokeAsync()
    {
        if (_running is { } active)
        {
            active.Cancel();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _running = cancel;
        var start = Stopwatch.GetTimestamp();
        try
        {
            VisualIntelligenceLog.Started(_logger);
            // Asked about each time when the user chose that: the capture below runs under this one answer and no other.
            var grant = await _gate.RequestAsync(
                PermissionCapability.ScreenCapture, "You used the Visual Intelligence shortcut. Part of the screen is captured into memory, and nothing is saved.", cancel.Token)
                .ConfigureAwait(true);
            if (!grant.IsGranted)
            {
                VisualIntelligenceLog.NotAllowed(_logger, grant.Decision.Reason);
                Tell(grant.Decision.Reason is PermissionDecisionReason.Declined or PermissionDecisionReason.CouldNotAsk ? PermissionTexts.WhyNot(grant.Decision) + " Nothing was captured." : TurnedOffText);
                return;
            }


            // The Assistant's window is not part of what is captured: it goes first, and Windows is given a moment to take it off.
            if (_assistant.HideNow())
            {
                await Task.Delay(HideSettle, cancel.Token).ConfigureAwait(true);
            }

            IReadOnlyList<CapturedImage> snapshots;
            try
            {
                // The user's yes (when the permission asks each time) is for the capture and nothing after it: the overlay, and the question asked about the part chosen, run without it.
                using (grant.Enter())
                {
                    snapshots = await _capture.CaptureAllMonitorsAsync(cancel.Token).ConfigureAwait(true);
                }
            }
            catch (ScreenCaptureException failure)
            {
                VisualIntelligenceLog.CaptureFailed(_logger, failure.Failure, failure.ErrorCode);
                Tell(CaptureFailedText);
                return;
            }

            // The snapshots are wiped as soon as the overlay is done with them, whatever way it ends (the overlay does it itself when it
            // closes; this is for a failure before it was shown, or one that kept it from closing).
            CaptureOutcome? chosen;
            try
            {
                var chip = await _imageSearch(cancel.Token).ConfigureAwait(true);
                var showing = _overlay.SelectAsync(snapshots, chip, cancel.Token);
                VisualIntelligenceLog.OverlayShown(_logger, snapshots.Count, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                chosen = await showing.ConfigureAwait(true);
            }
            finally
            {
                foreach (var snapshot in snapshots)
                {
                    snapshot.Dispose();
                }
            }

            using var outcome = chosen;
            if (outcome is null)
            {
                VisualIntelligenceLog.GivenUp(_logger);
                return;
            }

            VisualIntelligenceLog.Chose(_logger, outcome.Action, outcome.Region.Width, outcome.Region.Height);
            await ActAsync(outcome).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            VisualIntelligenceLog.GivenUp(_logger);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failure here must not take the Assistant down: it is logged by type, and the user's screen is left as it was.
            VisualIntelligenceLog.Failed(_logger, exception.GetType().Name);
        }
        finally
        {
            _running = null;
        }
    }

    private async Task ActAsync(CaptureOutcome outcome)
    {
        switch (outcome.Action)
        {
            case CaptureAction.Copy:
                if (!_clipboard.TrySetImage(SnapshotImage.From(outcome.Region)))
                {
                    VisualIntelligenceLog.ClipboardBusy(_logger);
                    Tell(ClipboardBusyText);
                }

                break;
            case CaptureAction.Ask:
                await AskAsync(outcome).ConfigureAwait(true);
                break;
            case CaptureAction.ImageSearch:
                if (_searchImage is not null)
                {
                    await _searchImage(outcome.Region).ConfigureAwait(true);
                }

                break;
        }
    }

    // The selected part becomes a conversation of its own: the part attached, and the composer ready; with a question typed in the chip,
    // the question is asked about it at once.
    private async Task AskAsync(CaptureOutcome outcome)
    {
        var png = await CapturedImageEncoder.EncodePngAsync(outcome.Region).ConfigureAwait(true);
        var image = ImageItem.FromCapture(
            CaptureName, png, SnapshotImage.Thumbnail(outcome.Region, ThumbnailSide), outcome.Region.Width, outcome.Region.Height);
        _conversation.StartWithCapture(image);
        if (outcome.Question.Length > 0)
        {
            _conversation.Ask(outcome.Question);
        }

        _windows.ShowConversation();
    }

    // Says why nothing happened, in the floating conversation.
    private void Tell(string text)
    {
        _conversation.StartWithNotice(text);
        _windows.ShowConversation();
    }
}
