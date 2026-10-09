using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Selection;

/// <summary>Copy-fallback log messages: statuses, program names and sizes only, never the copied text, the clipboard's contents or a window title.</summary>
internal static partial class CopySelectionLog
{
    [LoggerMessage(EventId = 5710, Level = LogLevel.Information,
        Message = "Selection copy from {Process}: {Status}, clipboard {Restore} ({Length} characters, truncated: {IsTruncated}) in {ElapsedMs} ms")]
    public static partial void Answered(
        ILogger logger, string process, CopySelectionStatus status, ClipboardRestoreOutcome restore, int length, bool isTruncated, long elapsedMs);

    [LoggerMessage(EventId = 5711, Level = LogLevel.Information, Message = "Copy was not pressed: the clipboard cannot be saved exactly ({Reason})")]
    public static partial void NotSaved(ILogger logger, SnapshotFailure reason);

    [LoggerMessage(EventId = 5712, Level = LogLevel.Warning, Message = "The Copy key press could not be sent")]
    public static partial void NotSent(ILogger logger);

    [LoggerMessage(EventId = 5713, Level = LogLevel.Information, Message = "The clipboard changed after the copy, so the previous contents were not put back over it")]
    public static partial void ClipboardChangedMeanwhile(ILogger logger);

    [LoggerMessage(EventId = 5714, Level = LogLevel.Warning, Message = "The previous clipboard could not be put back")]
    public static partial void NotRestored(ILogger logger);

    [LoggerMessage(EventId = 5715, Level = LogLevel.Warning, Message = "Selection copy failed ({ExceptionType})")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5716, Level = LogLevel.Warning, Message = "Selection copy did not finish in time and was given up on")]
    public static partial void TimedOut(ILogger logger);
}
