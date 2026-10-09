using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Selection;

/// <summary>Selection log messages: statuses, program names and sizes only, never the selected text or a window title.</summary>
internal static partial class SelectionLog
{
    [LoggerMessage(EventId = 5700, Level = LogLevel.Debug,
        Message = "Selection from {Process}: {Status} ({Length} characters, truncated: {IsTruncated}) in {ElapsedMs} ms")]
    public static partial void Answered(ILogger logger, string process, SelectionStatus status, int length, bool isTruncated, long elapsedMs);

    [LoggerMessage(EventId = 5701, Level = LogLevel.Debug, Message = "Nothing was asked: no window is in the foreground")]
    public static partial void NoForeground(ILogger logger);

    [LoggerMessage(EventId = 5702, Level = LogLevel.Debug,
        Message = "{Process} does not expose its selection to UI Automation: {Reason}")]
    public static partial void Unsupported(ILogger logger, string process, SelectionProbeOutcome reason);

    [LoggerMessage(EventId = 5703, Level = LogLevel.Debug,
        Message = "The focused control belongs to another process than the foreground window of {Process}, so its selection was not read")]
    public static partial void FocusElsewhere(ILogger logger, string process);

    [LoggerMessage(EventId = 5704, Level = LogLevel.Debug, Message = "UI Automation failed reading {Process}'s selection: {ExceptionType}")]
    public static partial void ProbeFailed(ILogger logger, string process, string exceptionType);
}
