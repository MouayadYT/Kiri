using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Hotkeys;

internal static partial class HotkeyLog
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning,
        Message = "Global hotkey {HotkeyId} registration failed (Win32 error: {ErrorCode}); the overlay remains available manually")]
    public static partial void RegistrationFailed(ILogger logger, int hotkeyId, int errorCode);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
        Message = "Global hotkey {HotkeyId} configuration is invalid; the overlay remains available manually")]
    public static partial void InvalidConfiguration(ILogger logger, int hotkeyId);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning,
        Message = "Global hotkey {HotkeyId} could not be unregistered (Win32 error: {ErrorCode})")]
    public static partial void UnregistrationFailed(ILogger logger, int hotkeyId, int errorCode);
}
