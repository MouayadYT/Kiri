namespace Assistant.Core.QuickSearch.Actions;

/// <summary>How a quick action ended.</summary>
/// <param name="Succeeded">Whether it did what it is for.</param>
/// <param name="Message">What to tell the user when it did not ("Couldn't change the volume."); never private content.</param>
public sealed record QuickActionOutcome(bool Succeeded, string? Message = null)
{
    /// <summary>It did what it is for.</summary>
    public static QuickActionOutcome Done { get; } = new(true);

    /// <summary>It did not, and <paramref name="message"/> says so.</summary>
    public static QuickActionOutcome Failed(string message) => new(false, message);
}

/// <summary>
/// Runs the quick actions the app has wired (PROJECT_SPEC §4.1). It is what makes an action <see cref="QuickActionAvailability.Available"/>
/// in fact: an action nothing can run is never listed. The user's own choice from the bar is what runs one, and it never runs for
/// anything else: the model cannot run an action (PROJECT_SPEC §4.8), and the executor refuses any action that is not defined as safe
/// to run, whatever it is given.
/// </summary>
public interface IQuickActionExecutor
{
    /// <summary>Whether the action <paramref name="actionId"/> is wired here and is safe to run.</summary>
    bool CanRun(string actionId);

    /// <summary>
    /// Runs the action. An action that is not wired or not safe, an argument that is not valid, and a failure are an outcome that
    /// did not succeed, never an exception.
    /// </summary>
    /// <param name="actionId">The action's id.</param>
    /// <param name="argument">The number the action was given, as text, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    Task<QuickActionOutcome> RunAsync(string actionId, string? argument, CancellationToken cancellationToken = default);
}

/// <summary>A folder of the user's that a quick action opens.</summary>
public enum SystemFolder
{
    /// <summary>The user's own folder.</summary>
    Home = 0,

    /// <summary>The Desktop folder.</summary>
    Desktop = 1,

    /// <summary>The Documents folder.</summary>
    Documents = 2,

    /// <summary>The Downloads folder.</summary>
    Downloads = 3,

    /// <summary>The Pictures folder.</summary>
    Pictures = 4,

    /// <summary>The Music folder.</summary>
    Music = 5,

    /// <summary>The Videos folder.</summary>
    Videos = 6,
}

/// <summary>The default speakers' volume and mute switch.</summary>
/// <param name="Percent">The volume, 0 to 100.</param>
/// <param name="Muted">Whether the sound is off.</param>
public readonly record struct VolumeState(int Percent, bool Muted);

/// <summary>
/// What the quick actions do to Windows itself (PROJECT_SPEC §4.1): a small, closed set of deterministic operations. Nothing here
/// runs a command line, a script or a program the caller names: there is no way to ask for anything outside this list. The tools the
/// model may call to change the volume or open a folder of the user's (PROJECT_SPEC §4.8) go through the same list.
/// </summary>
public interface ISystemActions
{
    /// <summary>Opens the Windows Settings app.</summary>
    /// <returns><see langword="false"/> when it could not be opened.</returns>
    bool OpenWindowsSettings();

    /// <summary>Locks this PC's session, as Win+L does.</summary>
    /// <returns><see langword="false"/> when Windows refused.</returns>
    bool LockWorkstation();

    /// <summary>Turns the default speakers' sound off or on.</summary>
    /// <returns><see langword="false"/> when there is no output device or it could not be changed.</returns>
    bool SetMuted(bool muted);

    /// <summary>
    /// Sets the default speakers' volume to <paramref name="percent"/> (0 to 100, anything else is cut to it), and turns the sound on
    /// when it is more than nothing, as the volume keys do.
    /// </summary>
    /// <returns><see langword="false"/> when there is no output device or it could not be changed.</returns>
    bool SetVolume(int percent);

    /// <summary>Raises or lowers the volume by <paramref name="percent"/> points, within 0 to 100, and turns the sound on when it is raised.</summary>
    /// <returns>The volume it is now, or <see langword="null"/> when there is no output device or it could not be changed.</returns>
    int? ChangeVolume(int percent);

    /// <summary>The default speakers' volume and whether the sound is off, or <see langword="null"/> when there is no output device or it could not be read.</summary>
    VolumeState? GetVolume();

    /// <summary>Whether Windows' Do not disturb is on, or <see langword="null"/> when Windows does not say.</summary>
    bool? GetDoNotDisturb() => null;

    /// <summary>Turns Windows' Do not disturb on or off: notifications are held back while it is on, and nothing else changes.</summary>
    /// <returns><see langword="false"/> when Windows could not be told, or does not show it that way afterwards.</returns>
    bool SetDoNotDisturb(bool on) => false;

    /// <summary>The full path of a folder of the user's, or <see langword="null"/> when Windows does not have it.</summary>
    string? GetFolder(SystemFolder folder);
}
