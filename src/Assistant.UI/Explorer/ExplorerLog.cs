using Microsoft.Extensions.Logging;

namespace Assistant.UI.Explorer;

/// <summary>File Explorer integration's log messages: counts and outcomes, never a path or a file name (PROJECT_SPEC §3.3).</summary>
internal static partial class ExplorerLog
{
    [LoggerMessage(EventId = 9010, Level = LogLevel.Information, Message = "File Explorer sent {FileCount} files: {PictureCount} pictures and {DocumentCount} documents attached, notice {HasNotice}")]
    public static partial void FilesReceived(ILogger logger, int fileCount, int pictureCount, int documentCount, bool hasNotice);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Error, Message = "File Explorer's files could not be handed to the window")]
    public static partial void DeliveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Error, Message = "The app pipe stopped serving")]
    public static partial void ServingFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9013, Level = LogLevel.Information, Message = "File Explorer menu entry refreshed: {Succeeded}")]
    public static partial void MenuRefreshed(ILogger logger, bool succeeded);

    [LoggerMessage(EventId = 9015, Level = LogLevel.Information, Message = "The same selection from File Explorer arrived again and was ignored")]
    public static partial void RepeatIgnored(ILogger logger);

    [LoggerMessage(EventId = 9014, Level = LogLevel.Warning, Message = "File Explorer menu entry could not be refreshed")]
    public static partial void MenuRefreshFailed(ILogger logger, Exception exception);
}
