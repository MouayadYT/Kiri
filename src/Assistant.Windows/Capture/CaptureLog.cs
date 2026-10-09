using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Capture;

/// <summary>Screen capture log messages: kinds, sizes and times only, never pixels, window titles or anything on the screen.</summary>
internal static partial class CaptureLog
{
    [LoggerMessage(EventId = 5600, Level = LogLevel.Debug,
        Message = "Captured a {Kind} by {Method}: {Width} x {Height} pixels in {ElapsedMs} ms")]
    public static partial void Captured(ILogger logger, CaptureKind kind, CaptureMethod method, int width, int height, long elapsedMs);

    [LoggerMessage(EventId = 5601, Level = LogLevel.Warning,
        Message = "A {Kind} capture failed: {Failure} (Win32 error: {ErrorCode})")]
    public static partial void Failed(ILogger logger, CaptureKind kind, ScreenCaptureFailure failure, int errorCode);

    [LoggerMessage(EventId = 5602, Level = LogLevel.Debug,
        Message = "The window could not draw itself for the capture (Win32 error: {ErrorCode}); the screen is copied where it is instead")]
    public static partial void WindowFellBack(ILogger logger, int errorCode);
}
