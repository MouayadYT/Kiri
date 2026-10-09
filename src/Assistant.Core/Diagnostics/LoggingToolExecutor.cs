using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Diagnostics;

/// <summary>
/// Logs the outcome of every tool call made through another <see cref="IToolExecutor"/>: tool name, risk level,
/// status and duration, never arguments or output.
/// </summary>
public sealed partial class LoggingToolExecutor(
    IToolExecutor inner,
    IToolRegistry registry,
    ILogger<LoggingToolExecutor> logger)
    : IToolExecutor
{
    private const string UnknownToolName = "(unknown)";

    /// <inheritdoc/>
    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
        ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

    /// <inheritdoc/>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // The requested name is model output, so only a registered tool's name is safe to log.
        var tool = registry.Find(call.ToolName);
        var toolName = tool?.Name ?? UnknownToolName;
        var start = Stopwatch.GetTimestamp();

        try
        {
            var result = await inner.ExecuteAsync(call, context, cancellationToken).ConfigureAwait(false);
            var level = result.Status == ToolResultStatus.Failed ? LogLevel.Warning : LogLevel.Information;
            LogToolCompleted(logger, level, toolName, tool?.RiskLevel, result.Status, ElapsedMs(start));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogToolCompleted(logger, LogLevel.Information, toolName, tool?.RiskLevel, ToolResultStatus.Cancelled, ElapsedMs(start));
            throw;
        }
        catch (Exception exception)
        {
            LogToolThrew(logger, toolName, tool?.RiskLevel, ElapsedMs(start), exception);
            throw;
        }
    }

    private static long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    [LoggerMessage(EventId = 3000, Message = "Tool {ToolName} ({RiskLevel}) finished with {Status} in {ElapsedMs} ms")]
    private static partial void LogToolCompleted(
        ILogger logger,
        LogLevel level,
        string toolName,
        RiskLevel? riskLevel,
        ToolResultStatus status,
        long elapsedMs);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "Tool {ToolName} ({RiskLevel}) threw after {ElapsedMs} ms")]
    private static partial void LogToolThrew(
        ILogger logger,
        string toolName,
        RiskLevel? riskLevel,
        long elapsedMs,
        Exception exception);
}
