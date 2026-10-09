namespace Assistant.Windows.Selection;

/// <summary>
/// The application the user is in: the one whose window is in the foreground. It names the program, never what the program is
/// showing, so it is safe to log (PROJECT_SPEC §3.2).
/// </summary>
/// <param name="ProcessId">The id of the application's process.</param>
/// <param name="ProcessName">The process's name without <c>.exe</c> (for example <c>notepad</c>), or an empty string when Windows would not say.</param>
/// <param name="ExecutablePath">The full path of the program's executable, or <see langword="null"/> when Windows would not say (a protected process).</param>
/// <param name="WindowHandle">The foreground window's handle, which is only good while that window exists.</param>
public sealed record ForegroundApp(int ProcessId, string ProcessName, string? ExecutablePath, nint WindowHandle);
