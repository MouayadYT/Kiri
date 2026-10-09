using System.Windows;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>Application lifecycle log messages.</summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Assistant {Version} starting in {EnvironmentName}")]
    public static partial void Starting(ILogger logger, Version? version, string environmentName);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "Local data directories ready (first run: {FirstRun})")]
    public static partial void LocalDataReady(ILogger logger, bool firstRun);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Assistant started; Search or Ask bar shown after {ElapsedMs} ms")]
    public static partial void Started(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Windows session ending ({Reason})")]
    public static partial void SessionEnding(ILogger logger, ReasonSessionEnding reason);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Shutdown requested through the host")]
    public static partial void ShutdownRequested(ILogger logger);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Assistant stopping with exit code {ExitCode}")]
    public static partial void Stopping(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Assistant stopped after {UptimeSeconds:F1} s")]
    public static partial void Stopped(ILogger logger, double uptimeSeconds);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Critical, Message = "Unhandled exception during startup or on the UI thread; the application will exit")]
    public static partial void Fatal(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Critical, Message = "Unhandled exception on a background thread (terminating: {IsTerminating})")]
    public static partial void UnhandledBackgroundException(ILogger logger, bool isTerminating, Exception? exception);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Error, Message = "Unobserved exception in a background task")]
    public static partial void UnobservedTaskException(ILogger logger, Exception exception);
}
