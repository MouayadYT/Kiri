using Assistant.Core.Contracts;
using Assistant.Search.Planning;
using Microsoft.Extensions.Logging;

namespace Assistant.Search;

/// <summary>File search log messages. None of them carry the query, a name, a path or anything found.</summary>
internal static partial class SearchLog
{
    [LoggerMessage(EventId = 5500, Level = LogLevel.Debug,
        Message = "File search returned {Returned} of {Candidates} candidates for {Kinds} kinds in {DurationMs} ms")]
    public static partial void Completed(ILogger logger, int returned, int candidates, int kinds, long durationMs);

    [LoggerMessage(EventId = 5501, Level = LogLevel.Warning,
        Message = "File search failed ({Failure}, provider error 0x{ErrorCode:X8}) after {DurationMs} ms")]
    public static partial void Failed(ILogger logger, FileSearchFailure failure, int errorCode, long durationMs);

    [LoggerMessage(EventId = 5502, Level = LogLevel.Debug,
        Message = "Content search is {Support} for this query ({Limits})")]
    public static partial void ContentSearchLimited(ILogger logger, ContentSearchSupport support, ContentSearchLimits limits);
}

/// <summary>
/// Search planning and request log messages. None of them carry the request, the model's reply, a plan, a name, a path or
/// anything found: only where a plan came from, how many words and candidates there were, why the model was no use, and how
/// long it took.
/// </summary>
internal static partial class PlanningLog
{
    [LoggerMessage(EventId = 5510, Level = LogLevel.Debug,
        Message = "Planned a file search from {Source} with {Words} words of a name in {DurationMs} ms")]
    public static partial void Planned(ILogger logger, FileSearchPlanSource source, int words, long durationMs);

    [LoggerMessage(EventId = 5511, Level = LogLevel.Warning,
        Message = "The model could not judge the files found ({ExceptionType}); the search goes on without it")]
    public static partial void ModelFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5512, Level = LogLevel.Debug,
        Message = "File request ended {Status} with {Items} items from a {Source} plan in {DurationMs} ms")]
    public static partial void RequestEnded(
        ILogger logger, FileRequestStatus status, int items, FileSearchPlanSource? source, long durationMs);

    [LoggerMessage(EventId = 5513, Level = LogLevel.Debug,
        Message = "Second look at a file request: {Candidates} candidates, {Full} holding every word, {Partial} some, {Picked} picked in {DurationMs} ms")]
    public static partial void SecondLook(ILogger logger, int candidates, int full, int partial, int picked, long durationMs);

    [LoggerMessage(EventId = 5514, Level = LogLevel.Debug,
        Message = "The model's pick of {Candidates} candidates was no use ({Reason}) after {DurationMs} ms")]
    public static partial void ReviewUnusable(ILogger logger, ModelReplyFailure reason, int candidates, long durationMs);
}
