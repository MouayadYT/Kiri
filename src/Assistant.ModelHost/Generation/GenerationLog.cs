using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Generation;

/// <summary>
/// The generator's operational log: outcomes, counts and timings, never the prompt, the answer or what the engine said
/// (PROJECT_SPEC §3.3).
/// </summary>
internal static partial class GenerationLog
{
    [LoggerMessage(EventId = 2360, Level = LogLevel.Information, Message = "Generation refused ({ErrorCode})")]
    public static partial void Refused(ILogger logger, ModelHostErrorCode errorCode);

    [LoggerMessage(EventId = 2361, Level = LogLevel.Information, Message = "Generation ended ({Reason}): {PieceCount} pieces of text and {ToolCallCount} tool calls, {PromptTokens} prompt and {OutputTokens} output tokens, first text after {FirstTextMs} ms, {ElapsedMs} ms in all")]
    public static partial void Completed(
        ILogger logger,
        GenerationStopReason reason,
        int pieceCount,
        int toolCallCount,
        int? promptTokens,
        int? outputTokens,
        long? firstTextMs,
        long elapsedMs);

    [LoggerMessage(EventId = 2362, Level = LogLevel.Information, Message = "Generation stopped by the owner after {PieceCount} pieces of text in {ElapsedMs} ms")]
    public static partial void Stopped(ILogger logger, int pieceCount, long elapsedMs);

    [LoggerMessage(EventId = 2363, Level = LogLevel.Warning, Message = "Generation failed ({ErrorCode}) after {PieceCount} pieces of text in {ElapsedMs} ms")]
    public static partial void Failed(ILogger logger, ModelHostErrorCode errorCode, int pieceCount, long elapsedMs);
}
