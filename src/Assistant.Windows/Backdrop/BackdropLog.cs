using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Backdrop;

/// <summary>Backdrop log messages. None of them include window content.</summary>
internal static partial class BackdropLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Blurred backdrops are unavailable; glass surfaces draw opaque")]
    public static partial void Unavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug, Message = "Backdrop created (blurred: {IsBlurred})")]
    public static partial void Created(ILogger logger, bool isBlurred);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Backdrop blur changed (blurred: {IsBlurred})")]
    public static partial void BlurChanged(ILogger logger, bool isBlurred);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "Backdrop failed while following its window")]
    public static partial void FollowFailed(ILogger logger, Exception exception);
}
