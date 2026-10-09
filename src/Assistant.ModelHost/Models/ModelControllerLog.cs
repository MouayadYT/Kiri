using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Models;

/// <summary>The model controller's operational log: statuses, failure reasons and durations, never a path or model file name.</summary>
internal static partial class ModelControllerLog
{
    [LoggerMessage(EventId = 2350, Level = LogLevel.Information, Message = "Model status is now {Status} (change {Sequence})")]
    public static partial void StatusChanged(ILogger logger, ModelStatus status, long sequence);

    [LoggerMessage(EventId = 2351, Level = LogLevel.Warning, Message = "Model status is now {Status} ({Failure}, change {Sequence})")]
    public static partial void StatusFailed(ILogger logger, ModelStatus status, ModelFailure failure, long sequence);

    [LoggerMessage(EventId = 2352, Level = LogLevel.Information, Message = "Model loaded in {ElapsedMs} ms with a context of {ContextTokens} tokens (vision: {Vision})")]
    public static partial void Loaded(ILogger logger, long elapsedMs, int contextTokens, bool vision);

    [LoggerMessage(EventId = 2353, Level = LogLevel.Warning, Message = "Model could not be loaded ({Failure})")]
    public static partial void LoadFailed(ILogger logger, ModelFailure failure);

    [LoggerMessage(EventId = 2354, Level = LogLevel.Information, Message = "Model load was replaced by a later request")]
    public static partial void LoadSuperseded(ILogger logger);

    [LoggerMessage(EventId = 2355, Level = LogLevel.Information, Message = "Model unloaded in {ElapsedMs} ms")]
    public static partial void Unloaded(ILogger logger, long elapsedMs);
}
