namespace Assistant.Core.Storage;

/// <summary>
/// Owns the app's local data locations (PROJECT_SPEC §3.5). Modules get their directories here instead of building
/// paths themselves.
/// </summary>
/// <remarks>
/// Every location is a child of <see cref="RootDirectory"/>, which defaults to
/// <c>%LOCALAPPDATA%\Assistant</c>. Constructing an instance does no I/O; <see cref="EnsureDirectoriesExist"/>
/// creates the directories.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>Name of the app's folder under the user's local application data.</summary>
    public const string AppFolderName = "Assistant";

    /// <summary>Name of the settings file, which lives directly inside <see cref="RootDirectory"/>.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>Name of the file that holds the connected apps the user has installed (step 104), which lives directly inside <see cref="RootDirectory"/>.</summary>
    public const string IntegrationsFileName = "integrations.json";

    private readonly string[] _directories;

    /// <summary>Creates the locations under <paramref name="rootDirectory"/>, for example a temporary folder in tests.</summary>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is not a fully qualified path.</exception>
    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The root directory must be a fully qualified path.", nameof(rootDirectory));
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        SettingsFilePath = Path.Combine(RootDirectory, SettingsFileName);
        IntegrationsFilePath = Path.Combine(RootDirectory, IntegrationsFileName);
        DatabaseDirectory = Path.Combine(RootDirectory, "data");
        ModelsDirectory = Path.Combine(RootDirectory, "models");
        VoicesDirectory = Path.Combine(RootDirectory, "voices");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        TemporaryCapturesDirectory = Path.Combine(RootDirectory, "captures");
        BrowserBridgeDirectory = Path.Combine(RootDirectory, "browser-bridge");
        PackagesDirectory = Path.Combine(RootDirectory, "packages");
        SocketsDirectory = Path.Combine(RootDirectory, "sockets");
        IntegrationsDirectory = Path.Combine(RootDirectory, "Integrations");
        RuntimesDirectory = Path.Combine(RootDirectory, "Runtimes");

        _directories =
        [
            RootDirectory,
            DatabaseDirectory,
            ModelsDirectory,
            VoicesDirectory,
            CacheDirectory,
            LogsDirectory,
            TemporaryCapturesDirectory,
            BrowserBridgeDirectory,
            PackagesDirectory,
            SocketsDirectory,
            IntegrationsDirectory,
            RuntimesDirectory,
        ];
    }

    /// <summary>The app's own folder; settings live directly inside it.</summary>
    public string RootDirectory { get; }

    /// <summary>The settings file (PROJECT_SPEC §3.5); a few files beside it are the copies the settings service keeps.</summary>
    public string SettingsFilePath { get; }

    /// <summary>
    /// The installed integrations (connected apps): what is needed to reconnect to each, and never a secret (PROJECT_SPEC section 3.5, step 104).
    /// Secrets are kept in Windows Credential Manager (<c>ISecretStore</c>) and only their names are written here.
    /// </summary>
    public string IntegrationsFilePath { get; }

    /// <summary>
    /// Where the integrations the Assistant installed live, one folder for each integration and one inside it for each version
    /// (<c>Integrations\&lt;id&gt;\&lt;version&gt;</c>), so that a version that was downloaded is reused and one that is replaced or removed leaves
    /// nothing behind (PROJECT_SPEC §4.8, step 108). The folder is the Assistant's own: nothing in it is the user's.
    /// </summary>
    public string IntegrationsDirectory { get; }

    /// <summary>
    /// Where the runtimes that integrations need (Node.js, Python) are kept, one folder for each runtime and version
    /// (<c>Runtimes\&lt;kind&gt;\&lt;version&gt;</c>), set up by the Assistant and used by it alone: the runtimes the user may have installed are never
    /// used or changed (PROJECT_SPEC §4.8, step 108).
    /// </summary>
    public string RuntimesDirectory { get; }

    /// <summary>The SQLite database with conversation history.</summary>
    public string DatabaseDirectory { get; }

    /// <summary>Default location of installed models, used unless the user picks another folder.</summary>
    public string ModelsDirectory { get; }

    /// <summary>
    /// Where voices the user adds themselves go (step 125), one folder for each engine (<c>voices\&lt;engine id&gt;</c>, with <c>speech-recognition</c> and
    /// <c>wake-word</c> for the recognizers): used before the voices that came packaged with the Assistant, as <see cref="ModelsDirectory"/> is for models.
    /// Nothing in it is checked against a manifest, because the user put it there.
    /// </summary>
    public string VoicesDirectory { get; }

    /// <summary>Data the app can rebuild at any time, so deleting it never loses anything.</summary>
    public string CacheDirectory { get; }

    /// <summary>Rolling, content-free log files (PROJECT_SPEC §3.3).</summary>
    public string LogsDirectory { get; }

    /// <summary>
    /// Short-lived files that an API can read only from disk. Each file must be deleted as soon as it is used.
    /// Screenshots and crops never go here; they stay in memory (PROJECT_SPEC §3.1, P7).
    /// </summary>
    public string TemporaryCapturesDirectory { get; }

    /// <summary>The browser native-messaging host manifest and related BrowserBridge files.</summary>
    public string BrowserBridgeDirectory { get; }

    /// <summary>Installer and update packages the app stages locally, such as a sparse package for package identity.</summary>
    public string PackagesDirectory { get; }

    /// <summary>
    /// Sockets that the app's own processes listen on, such as the model engine's (PROJECT_SPEC §5.6). Each is named for
    /// one launch and removed when its process exits. The folder is kept short: a socket's full path must fit in 107
    /// bytes.
    /// </summary>
    public string SocketsDirectory { get; }

    /// <summary>Every directory above, root first.</summary>
    public IReadOnlyList<string> Directories => _directories;

    /// <summary>Returns the locations for the current Windows user, under their local application data.</summary>
    /// <exception cref="InvalidOperationException">The user has no local application data folder.</exception>
    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new InvalidOperationException("The local application data folder is not available for this user.");
        }

        return new AppPaths(Path.Combine(localAppData, AppFolderName));
    }

    /// <summary>
    /// Creates every directory that does not exist yet. New directories inherit the user profile's permissions.
    /// Safe to call repeatedly, and from several processes at once.
    /// </summary>
    /// <returns><see langword="true"/> if the root directory did not exist before, which means this is the first run.</returns>
    public bool EnsureDirectoriesExist()
    {
        var firstRun = !Directory.Exists(RootDirectory);
        foreach (var directory in _directories)
        {
            Directory.CreateDirectory(directory);
        }

        return firstRun;
    }
}
