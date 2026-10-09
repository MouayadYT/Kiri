using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Audio;

/// <summary>Microphone log messages. None of them carry audio, levels, words or device names.</summary>
internal static partial class MicrophoneLog
{
    [LoggerMessage(EventId = 5300, Level = LogLevel.Debug, Message = "Microphone opening")]
    public static partial void Opening(ILogger logger);

    [LoggerMessage(EventId = 5301, Level = LogLevel.Debug, Message = "Microphone closed after {DurationMs} ms")]
    public static partial void Closed(ILogger logger, long durationMs);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning,
        Message = "The microphone could not be used ({MicrophoneFailure}, HRESULT 0x{ErrorCode:X8})")]
    public static partial void Failed(ILogger logger, MicrophoneFailure microphoneFailure, int errorCode);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Error, Message = "The microphone failed unexpectedly")]
    public static partial void Error(ILogger logger, Exception exception);
}
