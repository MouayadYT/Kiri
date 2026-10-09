namespace Assistant.Core.Startup;

/// <summary>Whether Windows will start the Assistant when the user signs in.</summary>
public enum LaunchAtLoginState
{
    /// <summary>There is no sign-in entry for the Assistant.</summary>
    Off = 0,

    /// <summary>There is an entry, and Windows starts it.</summary>
    On = 1,

    /// <summary>
    /// There is an entry, but the user turned it off in Windows (Settings > Apps > Startup, or Task Manager's Startup apps), which Windows
    /// respects until the user turns it on there or the Assistant is asked to turn it on again.
    /// </summary>
    TurnedOffInWindows = 2,
}

/// <summary>
/// Starts the Assistant when the user signs in to Windows (PROJECT_SPEC §4.9), in the background: the entry runs this copy of the app with
/// <see cref="LaunchOptions.BackgroundArgument"/>, so nothing is shown until the shortcut is used. It is the user's own entry, for the
/// current user, and needs no administrator rights.
/// </summary>
public interface ILaunchAtLogin
{
    /// <summary>What Windows will do at the next sign-in.</summary>
    LaunchAtLoginState GetState();

    /// <summary>
    /// Adds the entry, pointing at this copy of the app, or points an existing one at it. An entry the user turned off in Windows is turned
    /// on again, since the user asks for it now. Returns whether it worked.
    /// </summary>
    Task<bool> EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure the entry the user asked for is there and starts this copy of the app: adds it when it is missing and points it here when it
    /// pointed at another copy (the app was moved, or rebuilt elsewhere). An entry the user turned off in Windows stays off. Returns whether
    /// it worked.
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the entry. Returns whether it worked; there being none is a success.</summary>
    Task<bool> DisableAsync(CancellationToken cancellationToken = default);
}
