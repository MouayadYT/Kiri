using Microsoft.Extensions.Logging;

namespace Assistant.UI.Tray;

/// <summary>The notification-area icon's and start-at-sign-in's log messages: outcomes only, nothing the user said or typed (PROJECT_SPEC §3.3).</summary>
internal static partial class TrayLog
{
    [LoggerMessage(EventId = 9030, Level = LogLevel.Information, Message = "Notification-area icon shown: {Shown}")]
    public static partial void IconShown(ILogger logger, bool shown);

    [LoggerMessage(EventId = 9031, Level = LogLevel.Information, Message = "Tray command: {Command}")]
    public static partial void CommandRun(ILogger logger, string command);

    [LoggerMessage(EventId = 9032, Level = LogLevel.Error, Message = "A tray command failed")]
    public static partial void CommandFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9033, Level = LogLevel.Information, Message = "Assistant started with Windows after {ElapsedMs} ms; the bar stays hidden until the shortcut is used")]
    public static partial void StartedHidden(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 9034, Level = LogLevel.Information, Message = "Sign-in entry refreshed: {Succeeded}")]
    public static partial void SignInEntryRefreshed(ILogger logger, bool succeeded);

    [LoggerMessage(EventId = 9035, Level = LogLevel.Warning, Message = "The sign-in entry could not be refreshed")]
    public static partial void SignInEntryRefreshFailed(ILogger logger, Exception exception);
}
