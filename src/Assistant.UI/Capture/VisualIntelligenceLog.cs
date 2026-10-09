using Assistant.Core.Domain;
using Assistant.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Capture;

/// <summary>
/// Visual Intelligence log messages (PROJECT_SPEC §3.3): that it started, what stopped it, how long the overlay took and what the user
/// chose, never a picture, what was in it, a question or a window title.
/// </summary>
internal static partial class VisualIntelligenceLog
{
    [LoggerMessage(EventId = 2600, Level = LogLevel.Information, Message = "Visual Intelligence started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2601, Level = LogLevel.Information, Message = "Visual Intelligence was not started: Screen Capture is {Reason}")]
    public static partial void NotAllowed(ILogger logger, PermissionDecisionReason reason);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Warning, Message = "Visual Intelligence could not capture the screen: {Failure} (Win32 error: {ErrorCode})")]
    public static partial void CaptureFailed(ILogger logger, ScreenCaptureFailure failure, int errorCode);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Information, Message = "The Visual Intelligence overlay was up on {Monitors} monitors {ElapsedMs} ms after the shortcut")]
    public static partial void OverlayShown(ILogger logger, int monitors, long elapsedMs);

    [LoggerMessage(EventId = 2604, Level = LogLevel.Information, Message = "Visual Intelligence ended: {Outcome}, a selection of {Width} x {Height} pixels")]
    public static partial void Chose(ILogger logger, CaptureAction outcome, int width, int height);

    [LoggerMessage(EventId = 2605, Level = LogLevel.Information, Message = "Visual Intelligence was given up")]
    public static partial void GivenUp(ILogger logger);

    [LoggerMessage(EventId = 2606, Level = LogLevel.Warning, Message = "Visual Intelligence failed ({ExceptionType})")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2607, Level = LogLevel.Warning, Message = "The selection could not be put on the clipboard")]
    public static partial void ClipboardBusy(ILogger logger);
}
