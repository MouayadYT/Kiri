using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;

namespace Assistant.UI.Search;

/// <summary>What the Assistant's own quick actions do to the Assistant itself, as the bar's actions see it.</summary>
internal interface IAssistantCommands
{
    /// <summary>Clears the bar so a new question can be typed, which always starts a new conversation.</summary>
    void NewConversation();

    /// <summary>Opens the History window.</summary>
    void ShowHistory();

    /// <summary>Opens the Settings window.</summary>
    void OpenSettings();

    /// <summary>Starts Visual Intelligence: the screen is dimmed and the user selects a part of it to ask about.</summary>
    void TakeScreenshot();
}

/// <summary>
/// The app's <see cref="IQuickActionExecutor"/> (PROJECT_SPEC §4.1): wires the safe, built quick actions to what does them, and nothing
/// else. An action is run only when it is defined as available and safe (<see cref="QuickActionDefinition.IsSafeToRun"/>), so one that
/// is planned or refused by design is never run, however it is asked for, and an id it knows nothing of is refused. The actions that
/// change Windows (the volume, the lock) are the closed set of <see cref="ISystemActions"/>: no command line or program the caller
/// names can be run through it. A number given to an action must be a whole number within the action's own range. What is
/// asked for is never logged.
/// </summary>
internal sealed class QuickActionExecutor : IQuickActionExecutor
{
    /// <summary>How much "Volume up" and "Volume down" change the volume, in points.</summary>
    internal const int VolumeStep = 10;

    private readonly IQuickActionCatalog _catalog;
    private readonly Dictionary<string, Func<string?, QuickActionOutcome>> _handlers;
    private readonly HashSet<string> _onThePool;

    public QuickActionExecutor(
        IQuickActionCatalog catalog, ISystemActions system, IFileLauncher files, IAssistantCommands assistant,
        IClipboardHistory? clipboardHistory = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(assistant);

        _handlers = new Dictionary<string, Func<string?, QuickActionOutcome>>(StringComparer.Ordinal)
        {
            [QuickActionIds.NewConversation] = _ => Run(assistant.NewConversation),
            [QuickActionIds.ShowHistory] = _ => Run(assistant.ShowHistory),
            [QuickActionIds.OpenAssistantSettings] = _ => Run(assistant.OpenSettings),
            [QuickActionIds.TakeScreenshot] = _ => Run(assistant.TakeScreenshot),
            [QuickActionIds.OpenWindowsSettings] = _ => Of(system.OpenWindowsSettings(), "Windows Settings could not be opened."),
            [QuickActionIds.Mute] = _ => Of(system.SetMuted(true), "The sound could not be turned off."),
            [QuickActionIds.Unmute] = _ => Of(system.SetMuted(false), "The sound could not be turned on."),
            [QuickActionIds.VolumeUp] = _ => Of(system.ChangeVolume(VolumeStep) is not null, "The volume could not be changed."),
            [QuickActionIds.VolumeDown] = _ => Of(system.ChangeVolume(-VolumeStep) is not null, "The volume could not be changed."),
            [QuickActionIds.SetVolume] = argument => SetVolume(system, argument),
            [QuickActionIds.LockPc] = _ => Of(system.LockWorkstation(), "The PC could not be locked."),
            [QuickActionIds.OpenHome] = _ => OpenFolder(system, files, SystemFolder.Home),
            [QuickActionIds.OpenDesktop] = _ => OpenFolder(system, files, SystemFolder.Desktop),
            [QuickActionIds.OpenDocuments] = _ => OpenFolder(system, files, SystemFolder.Documents),
            [QuickActionIds.OpenDownloads] = _ => OpenFolder(system, files, SystemFolder.Downloads),
            [QuickActionIds.OpenPictures] = _ => OpenFolder(system, files, SystemFolder.Pictures),
            [QuickActionIds.OpenMusic] = _ => OpenFolder(system, files, SystemFolder.Music),
            [QuickActionIds.OpenVideos] = _ => OpenFolder(system, files, SystemFolder.Videos),
        };
        if (clipboardHistory is not null)
        {
            _handlers[QuickActionIds.ClearClipboardHistory] = _ => Run(clipboardHistory.Clear);
        }

        // These take a moment (COM, the shell) and have nothing to do with a window: they run on the thread pool. The rest touch the
        // Assistant's own windows, which only the UI thread may.
        _onThePool =
        [
            QuickActionIds.OpenWindowsSettings, QuickActionIds.Mute, QuickActionIds.Unmute, QuickActionIds.VolumeUp,
            QuickActionIds.VolumeDown, QuickActionIds.SetVolume, QuickActionIds.LockPc,
        ];
    }

    /// <inheritdoc/>
    public bool CanRun(string actionId) =>
        actionId is not null && _handlers.ContainsKey(actionId) && _catalog.Find(actionId) is { IsSafeToRun: true };

    /// <inheritdoc/>
    public async Task<QuickActionOutcome> RunAsync(string actionId, string? argument, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRun(actionId))
        {
            return QuickActionOutcome.Failed("That action is not available.");
        }

        var definition = _catalog.Find(actionId)!;
        if (definition.Parameter is { } parameter)
        {
            if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < parameter.Minimum || number > parameter.Maximum)
            {
                return QuickActionOutcome.Failed($"{parameter.Name} must be a whole number from {parameter.Minimum} to {parameter.Maximum}.");
            }
        }
        else if (argument is not null)
        {
            // An action that takes no number is given none.
            return QuickActionOutcome.Failed("That action takes no value.");
        }

        var handler = _handlers[actionId];
        try
        {
            return _onThePool.Contains(actionId)
                ? await Task.Run(() => handler(argument), cancellationToken).ConfigureAwait(true)
                : handler(argument);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return QuickActionOutcome.Failed("That could not be done.");
        }
    }

    private static QuickActionOutcome Run(Action action)
    {
        action();
        return QuickActionOutcome.Done;
    }

    private static QuickActionOutcome Of(bool succeeded, string failure) =>
        succeeded ? QuickActionOutcome.Done : QuickActionOutcome.Failed(failure);

    private static QuickActionOutcome SetVolume(ISystemActions system, string? argument) =>
        int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
            ? Of(system.SetVolume(percent), "The volume could not be changed.")
            : QuickActionOutcome.Failed("The volume must be a whole number from 0 to 100.");

    private static QuickActionOutcome OpenFolder(ISystemActions system, IFileLauncher files, SystemFolder folder) =>
        system.GetFolder(folder) is { } path
            ? Of(files.Open(path), "That folder could not be opened.")
            : QuickActionOutcome.Failed("Windows does not have that folder.");
}
