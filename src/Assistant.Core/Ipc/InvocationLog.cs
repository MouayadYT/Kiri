using Microsoft.Extensions.Logging;

namespace Assistant.Core.Ipc;

/// <summary>
/// The app pipe's log messages: actions, counts and outcomes, never a path or a file name (PROJECT_SPEC §3.3).
/// </summary>
internal static partial class InvocationLog
{
    [LoggerMessage(EventId = 9000, Level = LogLevel.Information, Message = "Listening for invocation requests")]
    public static partial void Listening(ILogger logger);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "The app pipe is held by another process; invocation requests are not served")]
    public static partial void PipeUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "{Action} request with {FileCount} files handled ({Error})")]
    public static partial void RequestHandled(ILogger logger, InvocationAction action, int fileCount, InvocationErrorCode? error);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Rejected an invocation request ({Error})")]
    public static partial void RequestRejected(ILogger logger, InvocationErrorCode error);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Error, Message = "{Action} request could not be handled")]
    public static partial void HandlerFailed(ILogger logger, InvocationAction action, Exception exception);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Warning, Message = "An invocation connection timed out")]
    public static partial void ConnectionTimedOut(ILogger logger);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Debug, Message = "An invocation connection broke")]
    public static partial void ConnectionBroken(ILogger logger, Exception exception);
}
