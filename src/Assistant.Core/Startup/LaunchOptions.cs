namespace Assistant.Core.Startup;

/// <summary>
/// How the Assistant was started (PROJECT_SPEC §4.9). The only thing the command line says is that it started with Windows: then the
/// nothing is shown, and the Assistant waits in the notification area for its shortcut. Every other start, such as the user opening
/// the app, shows its full window, or asks the Assistant that is already running to show it.
/// </summary>
/// <param name="StartHidden">Whether the Assistant starts with nothing on screen.</param>
public sealed record LaunchOptions(bool StartHidden)
{
    /// <summary>
    /// The argument the sign-in entry passes (<see cref="ILaunchAtLogin"/>). It is a switch of the app, not a path, so it cannot be
    /// mistaken for a file sent from File Explorer.
    /// </summary>
    public const string BackgroundArgument = "--background";

    /// <summary>Reads the command line (without the program's name).</summary>
    public static LaunchOptions Parse(IEnumerable<string>? arguments) =>
        new(arguments?.Any(argument => string.Equals(argument, BackgroundArgument, StringComparison.OrdinalIgnoreCase)) == true);
}
