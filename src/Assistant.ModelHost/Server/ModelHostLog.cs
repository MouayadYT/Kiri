using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Server;

/// <summary>
/// The model host's log messages: request types, ids, counts, durations and outcomes, never prompts, images or output
/// (PROJECT_SPEC §3.3).
/// </summary>
internal static partial class ModelHostLog
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Information, Message = "Model host {Version} starting (protocol {ProtocolVersion}, owner process {OwnerProcessId})")]
    public static partial void Starting(ILogger logger, Version? version, int protocolVersion, int ownerProcessId);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Owner connected after {ElapsedMs} ms")]
    public static partial void OwnerConnected(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information, Message = "Model host exiting ({ExitReason}) with code {ExitCode}")]
    public static partial void Exiting(ILogger logger, ModelHostExitReason exitReason, int exitCode);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Error, Message = "Model host pipe could not be created")]
    public static partial void PipeUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Warning, Message = "Rejected a frame for request {RequestId} ({ErrorCode})")]
    public static partial void FrameRejected(ILogger logger, long requestId, ModelHostErrorCode errorCode);

    [LoggerMessage(EventId = 2305, Level = LogLevel.Debug, Message = "{RequestType} {RequestId} handled in {ElapsedMs} ms")]
    public static partial void RequestHandled(ILogger logger, Type requestType, long requestId, long elapsedMs);

    [LoggerMessage(EventId = 2306, Level = LogLevel.Information, Message = "{RequestType} {RequestId} cancelled after {ElapsedMs} ms")]
    public static partial void RequestCancelled(ILogger logger, Type requestType, long requestId, long elapsedMs);

    [LoggerMessage(EventId = 2307, Level = LogLevel.Error, Message = "{RequestType} {RequestId} failed")]
    public static partial void RequestFailed(ILogger logger, Type requestType, long requestId, Exception exception);

    [LoggerMessage(EventId = 2308, Level = LogLevel.Warning, Message = "Owner broke the framing; closing the connection")]
    public static partial void FramingBroken(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2309, Level = LogLevel.Information, Message = "Shutdown requested with {RunningCount} requests running")]
    public static partial void ShutdownRequested(ILogger logger, int runningCount);

    [LoggerMessage(EventId = 2310, Level = LogLevel.Critical, Message = "Model host failed")]
    public static partial void Failed(ILogger logger, Exception exception);
}
