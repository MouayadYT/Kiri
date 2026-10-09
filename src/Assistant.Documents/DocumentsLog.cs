using Assistant.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents;

/// <summary>
/// Document reading log messages. None of them carry a path, a name, a title or any text of a document: only which reader
/// ran, how it ended, how much came out and how long it took.
/// </summary>
internal static partial class DocumentsLog
{
    [LoggerMessage(EventId = 8000, Level = LogLevel.Debug,
        Message = "Document reader {Component} ended {Status}: {Segments} segments, {Characters} characters, truncated {Truncated}, in {DurationMs} ms")]
    public static partial void Read(
        ILogger logger, string component, DocumentReadStatus status, int segments, int characters, bool truncated, long durationMs);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Debug,
        Message = "Document reader {Component} read the properties of a file and ended {Status} in {DurationMs} ms")]
    public static partial void ReadMetadata(ILogger logger, string component, DocumentReadStatus status, long durationMs);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Debug,
        Message = "Document reader {Component} could not read a file ({ExceptionType}), reported as {Status}")]
    public static partial void Failed(ILogger logger, string component, string exceptionType, DocumentReadStatus status);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Debug,
        Message = "Document reader {Component} skipped {Units} pages or slides it could not parse (last failure {ExceptionType})")]
    public static partial void UnitsSkipped(ILogger logger, string component, int units, string exceptionType);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Debug,
        Message = "No document reader handles a file with the extension {FileExtension}")]
    public static partial void NoReader(ILogger logger, string fileExtension);

    [LoggerMessage(EventId = 8010, Level = LogLevel.Debug,
        Message = "Document context from {Component}: {Passages} passages from {Characters} characters, {Selected} selected " +
            "({SelectedCharacters} characters, {Reason}, {QueryTerms} query terms), truncated {Truncated}, in {DurationMs} ms")]
    public static partial void ContextBuilt(
        ILogger logger, string component, int passages, int characters, int selected, int selectedCharacters,
        PassageSelectionReason reason, int queryTerms, bool truncated, long durationMs);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Debug,
        Message = "Document context from {Component} ended {Status} without text for a question")]
    public static partial void ContextFailed(ILogger logger, string component, DocumentReadStatus status);
}
