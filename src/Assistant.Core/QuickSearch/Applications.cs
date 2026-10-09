using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>
/// An application as Start lists it (PROJECT_SPEC §4.1): a desktop program, a packaged app or a shortcut that Windows shows among
/// the apps. Names and paths are private content (PROJECT_SPEC §3.2): they are never printed or logged.
/// </summary>
/// <param name="Id">
/// The application's launch identity, as Windows knows it: an application user model id for a packaged app
/// (<c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>), and for a desktop program the path-like id Start gives it. It is what
/// <see cref="IApplicationLauncher"/> is given, and it never changes while the app stays installed.
/// </param>
/// <param name="DisplayName">The name Start shows.</param>
public sealed record InstalledApplication(string Id, string DisplayName)
{
    /// <summary>
    /// The program's own file, when the app is a desktop program whose file is known and exists; <see langword="null"/> for a
    /// packaged app and for one whose file could not be worked out. It is what "Show in File Explorer" and "Copy path" use.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// The file whose icon stands for the application, when it is not the application's own entry: a shortcut on the Desktop to an application that
    /// Start lists too. The Desktop is where a person gives a shortcut an icon of their own choosing, and that is the icon they know it by.
    /// <see langword="null"/> for the application's own icon.
    /// </summary>
    public string? IconPath { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"HasExecutablePath = {ExecutablePath is not null}");
        return true;
    }
}

/// <summary>Reads the list of applications Windows shows in Start, on this PC.</summary>
public interface IApplicationSource
{
    /// <summary>Raised when applications were installed or removed, so the list read earlier may be out of date.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Reads the applications, packaged ones included, without duplicates of one launch identity. It is slow compared with a
    /// lookup (it asks the shell), so the app keeps the answer (<see cref="IApplicationCatalog"/>) and asks again only when needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken);
}

/// <summary>Draws an application's own icon.</summary>
public interface IApplicationIconSource
{
    /// <summary>
    /// The icon of the application with launch identity <paramref name="applicationId"/> as a PNG, large enough for a 36-pixel tile on
    /// a screen at twice the usual density, or <see langword="null"/> when it has none or could not be read. It never throws for that.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<byte[]?> GetIconAsync(string applicationId, CancellationToken cancellationToken);
}

/// <summary>
/// Starts an application the user chose from a result (PROJECT_SPEC §4.1): a direct user action, handed to the shell the way
/// Start's own tiles are, never a tool call. It is given only a launch identity that came from the list of installed applications.
/// </summary>
public interface IApplicationLauncher
{
    /// <summary>Starts the application with launch identity <paramref name="applicationId"/>.</summary>
    /// <returns><see langword="false"/> when it could not be started.</returns>
    bool Launch(string applicationId);
}

/// <summary>
/// The list of installed applications, kept so that a lookup while typing never waits for the shell (PROJECT_SPEC §4.1). The list
/// is read once in the background, kept in memory, and read again in the background when it has grown old or the PC says something
/// was installed or removed; until the new list is there, the old one answers.
/// </summary>
public interface IApplicationCatalog
{
    /// <summary>Raised, from any thread, when a new reading changed the list.</summary>
    event EventHandler? Changed;

    /// <summary>The applications read last, or none before the first reading has ended.</summary>
    IReadOnlyList<InstalledApplication> Current { get; }

    /// <summary>Whether the first reading has ended.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// The applications. The first call waits for the first reading; later ones answer from memory at once, and when the list is
    /// stale they also start a reading in the background.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken);

    /// <summary>Starts the first reading in the background, if it has not started, so the first search does not wait for it.</summary>
    void WarmUp();

    /// <summary>Says the list may be out of date: the next lookup starts a reading in the background.</summary>
    void Invalidate();
}
