using Assistant.Core.Domain;
using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Selection;

/// <summary>
/// Ask Selection log messages (PROJECT_SPEC §3.3): that the shortcut was used, what stopped it and how the capture came out, never the
/// selected text, a question or a window title.
/// </summary>
internal static partial class AskSelectionLog
{
    [LoggerMessage(EventId = 2700, Level = LogLevel.Information, Message = "Ask Selection started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2701, Level = LogLevel.Information, Message = "Ask Selection was not run: Selected Text is {Reason}")]
    public static partial void NotAllowed(ILogger logger, PermissionDecisionReason reason);

    [LoggerMessage(EventId = 2702, Level = LogLevel.Information,
        Message = "Ask Selection read {Status} from {Process} ({Length} characters, truncated: {IsTruncated})")]
    public static partial void Read(ILogger logger, SelectionStatus status, string process, int length, bool isTruncated);

    [LoggerMessage(EventId = 2703, Level = LogLevel.Information, Message = "Ask Selection ignored the shortcut: the Assistant's own window is in front")]
    public static partial void OwnWindow(ILogger logger);

    [LoggerMessage(EventId = 2704, Level = LogLevel.Information, Message = "Ask Selection opened the Ask panel with the selected text, quick actions offered")]
    public static partial void Opened(ILogger logger);

    [LoggerMessage(EventId = 2706, Level = LogLevel.Information, Message = "Ask Selection by copy started")]
    public static partial void CopyStarted(ILogger logger);

    [LoggerMessage(EventId = 2707, Level = LogLevel.Information, Message = "Ask Selection by copy was not run: Selected Text by Copy is {Reason}")]
    public static partial void CopyNotAllowed(ILogger logger, PermissionDecisionReason reason);

    [LoggerMessage(EventId = 2708, Level = LogLevel.Information,
        Message = "Ask Selection by copy came to {Status}, clipboard {Restore}, from {Process} ({Length} characters, truncated: {IsTruncated})")]
    public static partial void Copied(
        ILogger logger, CopySelectionStatus status, ClipboardRestoreOutcome restore, string process, int length, bool isTruncated);

    [LoggerMessage(EventId = 2705, Level = LogLevel.Warning, Message = "Ask Selection failed ({ExceptionType})")]
    public static partial void Failed(ILogger logger, string exceptionType);
}
