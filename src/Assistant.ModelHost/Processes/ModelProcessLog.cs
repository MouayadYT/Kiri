using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Processes;

/// <summary>
/// The model engine's operational log: process ids, states, exit codes, counts and durations. Nothing the engine prints
/// is logged as text (PROJECT_SPEC §3.3); its lines reach here only as <see cref="LlamaServerSignal"/>s.
/// </summary>
internal static partial class ModelProcessLog
{
    [LoggerMessage(EventId = 2330, Level = LogLevel.Information, Message = "Model engine process {ProcessId} launched")]
    public static partial void Launched(ILogger logger, int processId);

    [LoggerMessage(EventId = 2331, Level = LogLevel.Information, Message = "Model engine process {ProcessId} is ready after {ElapsedMs} ms")]
    public static partial void Ready(ILogger logger, int processId, long elapsedMs);

    [LoggerMessage(EventId = 2332, Level = LogLevel.Warning, Message = "Model engine did not start ({Failure}, exit code {ExitCode})")]
    public static partial void StartFailed(ILogger logger, ModelProcessFailure failure, int? exitCode);

    [LoggerMessage(EventId = 2333, Level = LogLevel.Warning, Message = "Model engine process {ProcessId} exited unexpectedly with code {ExitCode} after {UptimeMs} ms ({ErrorCount} errors and {WarningCount} warnings reported)")]
    public static partial void ExitedUnexpectedly(ILogger logger, int processId, int exitCode, long uptimeMs, int errorCount, int warningCount);

    [LoggerMessage(EventId = 2334, Level = LogLevel.Information, Message = "Restarting the model engine in {DelayMs} ms (restart {Restart} of {MaxRestarts})")]
    public static partial void Restarting(ILogger logger, long delayMs, int restart, int maxRestarts);

    [LoggerMessage(EventId = 2335, Level = LogLevel.Error, Message = "Model engine is not restarted again ({Failure})")]
    public static partial void GaveUp(ILogger logger, ModelProcessFailure failure);

    [LoggerMessage(EventId = 2336, Level = LogLevel.Information, Message = "Model engine process {ProcessId} stopped after {UptimeMs} ms")]
    public static partial void Stopped(ILogger logger, int processId, long uptimeMs);

    [LoggerMessage(EventId = 2337, Level = LogLevel.Information, Message = "Model engine process {ProcessId} loaded its model")]
    public static partial void ModelLoaded(ILogger logger, int processId);

    [LoggerMessage(EventId = 2338, Level = LogLevel.Debug, Message = "Model engine process {ProcessId} runs {ThreadCount} threads")]
    public static partial void Threads(ILogger logger, int processId, int threadCount);

    [LoggerMessage(EventId = 2339, Level = LogLevel.Information, Message = "Model engine process {ProcessId} serves {SlotCount} slots of {ContextTokens} tokens")]
    public static partial void Slots(ILogger logger, int processId, int slotCount, int contextTokens);

    [LoggerMessage(EventId = 2340, Level = LogLevel.Debug, Message = "Model engine process {ProcessId} evaluated {PromptTokens} prompt tokens in {ElapsedMs} ms")]
    public static partial void PromptEvaluated(ILogger logger, int processId, int promptTokens, double elapsedMs);

    [LoggerMessage(EventId = 2341, Level = LogLevel.Debug, Message = "Model engine process {ProcessId} generated {OutputTokens} tokens in {ElapsedMs} ms")]
    public static partial void Generated(ILogger logger, int processId, int outputTokens, double elapsedMs);

    [LoggerMessage(EventId = 2342, Level = LogLevel.Debug, Message = "Model engine process {ProcessId} reported a warning (text withheld)")]
    public static partial void ReportedWarning(ILogger logger, int processId);

    [LoggerMessage(EventId = 2343, Level = LogLevel.Debug, Message = "Model engine process {ProcessId} reported an error (text withheld)")]
    public static partial void ReportedError(ILogger logger, int processId);

    [LoggerMessage(EventId = 2344, Level = LogLevel.Warning, Message = "Model engine process {ProcessId} could not load the model")]
    public static partial void ModelLoadFailed(ILogger logger, int processId);

    [LoggerMessage(EventId = 2345, Level = LogLevel.Warning, Message = "Model engine process {ProcessId} could not open its socket")]
    public static partial void EndpointFailed(ILogger logger, int processId);

    [LoggerMessage(EventId = 2346, Level = LogLevel.Warning, Message = "Model engine process {ProcessId} ran out of memory")]
    public static partial void OutOfMemory(ILogger logger, int processId);

    [LoggerMessage(EventId = 2347, Level = LogLevel.Error, Message = "Model engine could not be launched")]
    public static partial void LaunchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2348, Level = LogLevel.Error, Message = "Model engine socket path is {ByteCount} bytes, over the {MaxBytes}-byte limit")]
    public static partial void SocketPathTooLong(ILogger logger, int byteCount, int maxBytes);

    [LoggerMessage(EventId = 2349, Level = LogLevel.Error, Message = "Model engine socket folder could not be created")]
    public static partial void SocketFolderUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2350, Level = LogLevel.Warning, Message = "Model engine processes cannot be tied to the host's lifetime")]
    public static partial void JobUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2351, Level = LogLevel.Error, Message = "Model engine could not be stopped")]
    public static partial void StopFailed(ILogger logger, Exception exception);
}
