using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.UI.Messages;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Capture;

/// <summary>
/// The app's <see cref="IScreenshotTaker"/> (PROJECT_SPEC §4.6, §4.8): what the <c>take_screenshot</c> tool does when the model asks for
/// a picture of the screen in a conversation. It is Visual Intelligence's capture without the selection: the Assistant's window goes
/// (and comes back as it was), a snapshot of the monitor the user is on is taken, and the picture is attached to the conversation as a
/// part of the screen the user can see and take off (<see cref="ScreenAttachments"/>), going with the next questions of it. Everything is in
/// memory: the snapshot is wiped as soon as the attached copy is made, nothing is written to disk, and the log holds sizes alone.
/// </summary>
/// <remarks>
/// <para>
/// The tool's executor has already checked that Screen Capture is allowed and that the user confirmed the call, so this does neither.
/// A conversation shown in the History window is not hidden: only the floating window is taken off the screen for the capture.
/// </para>
/// <para>
/// The window, its controller and the attachments are looked up when a screenshot is taken and not when the taker is made: they
/// depend on the answer provider, which depends on the tools, which depend on this.
/// </para>
/// </remarks>
internal sealed partial class ConversationScreenshotTaker : IScreenshotTaker
{
    /// <summary>What the chip and the tile call the picture, as for a part of the screen the user captured.</summary>
    internal const string CaptureName = VisualIntelligenceController.CaptureName;

    private readonly IScreenCapture _capture;
    private readonly IServiceProvider _services;
    private readonly ILogger<ConversationScreenshotTaker> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates the taker.</summary>
    /// <param name="capture">Takes the picture.</param>
    /// <param name="services">Where the Assistant's window, its controller and the attachments are found when they are needed.</param>
    /// <param name="logger">Receives what happened, never what was seen.</param>
    /// <param name="dispatcher">The UI thread's dispatcher; the application's by default.</param>
    public ConversationScreenshotTaker(
        IScreenCapture capture, IServiceProvider services, ILogger<ConversationScreenshotTaker> logger, Dispatcher? dispatcher = null)
    {
        _capture = capture;
        _services = services;
        _logger = logger;
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <inheritdoc/>
    public async Task<ScreenshotOutcome> TakeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // The window goes first, from the UI thread, once it is known which monitor the user is on (where the window is).
        var (hidden, monitor) = await OnUiAsync(() =>
        {
            var assistant = _services.GetRequiredService<IAssistantWindow>();
            var monitors = _capture.GetMonitors();
            var chosen = (assistant.SurfaceTop is { } point ? monitors.FirstOrDefault(candidate => Contains(candidate.Bounds, point)) : null)
                ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
                ?? monitors.FirstOrDefault();
            return (assistant.HideNow(), chosen);
        }).ConfigureAwait(false);

        CapturedImage snapshot;
        try
        {
            if (monitor is null)
            {
                Log.Failed(_logger, ScreenCaptureFailure.NothingToCapture, 0);
                return new ScreenshotOutcome(ScreenshotStatus.Failed);
            }

            // Windows is given a moment to take the window off the screen, as before Visual Intelligence's snapshot.
            if (hidden)
            {
                await Task.Delay(VisualIntelligenceController.HideSettle, cancellationToken).ConfigureAwait(false);
            }

            snapshot = await _capture.CaptureMonitorAsync(monitor, cancellationToken).ConfigureAwait(false);
        }
        catch (ScreenCaptureException failure)
        {
            Log.Failed(_logger, failure.Failure, failure.ErrorCode);
            return new ScreenshotOutcome(ScreenshotStatus.Failed);
        }
        finally
        {
            // The conversation comes back as it was, whether or not the capture worked, and even when the call was stopped.
            if (hidden)
            {
                await OnUiAsync(() =>
                {
                    _services.GetRequiredService<AssistantWindowStateController>().ShowConversation();
                    return true;
                }).ConfigureAwait(false);
            }
        }

        using (snapshot)
        {
            var png = await CapturedImageEncoder.EncodePngAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                var capture = ImageItem.FromCapture(
                    CaptureName, png, SnapshotImage.Thumbnail(snapshot, VisualIntelligenceController.ThumbnailSide), snapshot.Width, snapshot.Height);
                _services.GetRequiredService<ScreenAttachments>().AttachToConversation(conversationId, capture);
                return true;
            }).ConfigureAwait(false);
            Log.Taken(_logger, snapshot.Width, snapshot.Height);
            return new ScreenshotOutcome(ScreenshotStatus.Taken, snapshot.Width, snapshot.Height);
        }
    }

    private static bool Contains(ScreenRect bounds, ScreenPoint point) =>
        point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    // Work that touches windows and what they show runs on the UI thread; the capture and the encoding do not.
    private Task<T> OnUiAsync<T>(Func<T> work) =>
        _dispatcher.CheckAccess() ? Task.FromResult(work()) : _dispatcher.InvokeAsync(work).Task;

    private static partial class Log
    {
        [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "A screenshot of {Width} x {Height} pixels was taken for a conversation")]
        public static partial void Taken(ILogger logger, int width, int height);

        [LoggerMessage(EventId = 3011, Level = LogLevel.Warning, Message = "A screenshot for a conversation could not be taken: {Failure} (Win32 error: {ErrorCode})")]
        public static partial void Failed(ILogger logger, ScreenCaptureFailure failure, int errorCode);
    }
}
