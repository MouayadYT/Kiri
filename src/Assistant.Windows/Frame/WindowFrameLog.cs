using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Frame;

/// <summary>Window frame log messages. None of them include window content.</summary>
internal static partial class WindowFrameLog
{
    [LoggerMessage(EventId = 5400, Level = LogLevel.Debug,
        Message = "Window frame applied (backdrop: {Backdrop}, available: {HasBackdrop}, translucent: {IsTranslucent})")]
    public static partial void Applied(ILogger logger, SystemBackdropKind backdrop, bool hasBackdrop, bool isTranslucent);

    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Window frame translucency changed (translucent: {IsTranslucent})")]
    public static partial void TranslucencyChanged(ILogger logger, bool isTranslucent);

    [LoggerMessage(EventId = 5402, Level = LogLevel.Error, Message = "Window frame failed to follow a settings change")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);
}
