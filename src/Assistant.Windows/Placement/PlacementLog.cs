using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Placement;

/// <summary>Window placement log messages. None of them identify other applications' windows.</summary>
internal static partial class PlacementLog
{
    [LoggerMessage(EventId = 5200, Level = LogLevel.Debug,
        Message = "Overlay placed on the monitor chosen by {MonitorSource} at {Dpi} DPI")]
    public static partial void Placed(ILogger logger, MonitorSource monitorSource, int dpi);

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning,
        Message = "No monitor was found for the overlay; it keeps its previous position")]
    public static partial void NoMonitor(ILogger logger);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning,
        Message = "The overlay could not be moved (Win32 error: {ErrorCode}); it keeps its previous position")]
    public static partial void MoveFailed(ILogger logger, int errorCode);
}
