using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Runtime;

/// <summary>The runtime locator's log messages: states and counts, never paths (PROJECT_SPEC §3.3).</summary>
internal static partial class RuntimeLog
{
    [LoggerMessage(EventId = 2320, Level = LogLevel.Information, Message = "Model runtime is ready")]
    public static partial void Ready(ILogger logger);

    [LoggerMessage(EventId = 2321, Level = LogLevel.Warning, Message = "Model runtime is unavailable ({RuntimeState}, {MissingCount} files missing)")]
    public static partial void Unavailable(ILogger logger, ModelRuntimeState runtimeState, int missingCount);
}
