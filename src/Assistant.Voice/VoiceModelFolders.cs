using Assistant.Core.Assets;
using Assistant.Core.Storage;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>
/// Finds the files of a voice engine on this PC (PROJECT_SPEC §3.5, §4.2, steps 123 and 125). A voice the user put in their own voices folder
/// (<see cref="AppPaths.VoicesDirectory"/>) is used first and is not checked, because the user put it there; a voice that came packaged with the
/// Assistant is in <c>&lt;install&gt;\assets\voices</c>, and its files are checked against the package's manifest before the engine loads them. Never
/// holds a path in a message: what is thrown is safe to show.
/// </summary>
public sealed class VoiceModelFolders
{
    /// <summary>The group of the speech recognizer's files, in the voices folders.</summary>
    public const string SpeechRecognitionGroup = "speech-recognition";

    /// <summary>The group of the wake-word listener's files, in the voices folders.</summary>
    public const string WakeWordGroup = "wake-word";

    private readonly AppPaths _paths;
    private readonly IPackagedAssets? _packaged;

    /// <summary>Creates the finder over the user's voices folder and, if the app has packaged assets, their checks.</summary>
    public VoiceModelFolders(AppPaths paths, IPackagedAssets? packaged = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _packaged = packaged;
    }

    /// <summary>The folder the user's own voices go in (<see cref="AppPaths.VoicesDirectory"/>), whether or not it exists.</summary>
    public string UserVoicesDirectory => _paths.VoicesDirectory;

    /// <summary>Whether <paramref name="groupId"/> is on this PC in either place: the user's own folder has files for it, or it is packaged and its files are all there.</summary>
    public bool IsInstalled(string groupId) =>
        HasUserFiles(groupId) || (_packaged is not null && _packaged.Peek(AssetKind.Voice, groupId).IsInstalled);

    /// <summary>The folder of the user's own copy of <paramref name="groupId"/>, whether or not it exists.</summary>
    public string UserFolderOf(string groupId) => Path.Combine(_paths.VoicesDirectory, groupId);

    /// <summary>Whether the user's own voices folder has <paramref name="groupId"/>: a folder with at least one file in it.</summary>
    public bool HasUserFiles(string groupId)
    {
        var folder = UserFolderOf(groupId);
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The folder to load <paramref name="groupId"/> from: the user's own when it has files, and otherwise the packaged one once its checksums have
    /// been checked (they are checked here, so call it off the UI thread).
    /// </summary>
    /// <exception cref="VoiceEngineException">It is not installed, or the packaged copy does not match what was packaged.</exception>
    public string Resolve(string groupId)
    {
        if (HasUserFiles(groupId))
        {
            return UserFolderOf(groupId);
        }

        if (_packaged is null)
        {
            throw NotInstalled();
        }

        var state = _packaged.Peek(AssetKind.Voice, groupId);
        if (state.Status is AssetGroupStatus.NotPackaged or AssetGroupStatus.Missing)
        {
            throw NotInstalled();
        }

        try
        {
            state = _packaged.VerifyAsync(AssetKind.Voice, groupId).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new VoiceEngineException(
                VoiceEngineFailure.FilesFailedCheck, "The voice's files could not be read to check them. Close any program that has them open.", exception);
        }

        if (!state.IsVerified)
        {
            throw new VoiceEngineException(
                VoiceEngineFailure.FilesFailedCheck,
                "The voice's files don't match what was packaged with this Assistant. Reinstall the Assistant to restore them.");
        }

        return Path.Combine(_packaged.Paths.VoicesDirectory, groupId);
    }

    private static VoiceEngineException NotInstalled() =>
        new(VoiceEngineFailure.NotInstalled, "This voice isn't installed. Put its files in the Assistant's voices folder, or reinstall the Assistant with it.");
}
