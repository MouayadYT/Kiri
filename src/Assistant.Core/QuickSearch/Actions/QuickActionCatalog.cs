using Assistant.Core.Domain;

namespace Assistant.Core.QuickSearch.Actions;

/// <summary>The ids of the quick actions. An id is never reused for another action.</summary>
public static class QuickActionIds
{
    public const string NewConversation = "assistant.new-conversation";
    public const string ShowHistory = "assistant.show-history";
    public const string OpenAssistantSettings = "assistant.open-settings";
    public const string TakeScreenshot = "assistant.take-screenshot";
    public const string ClearClipboardHistory = "assistant.clear-clipboard-history";

    public const string OpenWindowsSettings = "windows.open-settings";

    public const string Mute = "sound.mute";
    public const string Unmute = "sound.unmute";
    public const string VolumeUp = "sound.volume-up";
    public const string VolumeDown = "sound.volume-down";
    public const string SetVolume = "sound.set-volume";

    public const string OpenHome = "folder.home";
    public const string OpenDesktop = "folder.desktop";
    public const string OpenDocuments = "folder.documents";
    public const string OpenDownloads = "folder.downloads";
    public const string OpenPictures = "folder.pictures";
    public const string OpenMusic = "folder.music";
    public const string OpenVideos = "folder.videos";

    public const string LockPc = "session.lock";
    public const string Sleep = "session.sleep";
    public const string SignOut = "session.sign-out";
    public const string Restart = "session.restart";
    public const string ShutDown = "session.shut-down";
    public const string EmptyRecycleBin = "windows.empty-recycle-bin";
}

/// <summary>The quick actions the bar knows of, for <see cref="QuickActionCatalog"/> to hold.</summary>
public interface IQuickActionCatalog
{
    /// <summary>Every action that is defined, whether or not it is wired, in the order they are listed.</summary>
    IReadOnlyList<QuickActionDefinition> All { get; }

    /// <summary>The action with <paramref name="id"/>, or <see langword="null"/>.</summary>
    QuickActionDefinition? Find(string id);
}

/// <summary>
/// The quick actions (PROJECT_SPEC §4.1): every one the bar may ever list, defined with what it is, how risky it is and whether it is
/// built. Only an action that is <see cref="QuickActionAvailability.Available"/> is listed or run, and the catalog refuses to be
/// made with a destructive action that is not <see cref="QuickActionAvailability.Refused"/>, so that no change to the list can make
/// the app do something that loses the user's work.
/// </summary>
public sealed class QuickActionCatalog : IQuickActionCatalog
{
    private readonly Dictionary<string, QuickActionDefinition> _byId = new(StringComparer.Ordinal);

    /// <summary>Creates a catalog of <paramref name="definitions"/>.</summary>
    /// <exception cref="ArgumentException">An id is used twice, or a destructive action is not refused.</exception>
    public QuickActionCatalog(IEnumerable<QuickActionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        All = [.. definitions];
        foreach (var definition in All)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Title))
            {
                throw new ArgumentException("An action has an id and a title.", nameof(definitions));
            }

            if (definition.Risk == RiskLevel.Destructive && definition.Availability != QuickActionAvailability.Refused)
            {
                throw new ArgumentException("An action that can destroy data is always refused.", nameof(definitions));
            }

            if (!_byId.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException("Two actions have the same id.", nameof(definitions));
            }
        }
    }

    /// <summary>The app's own catalog.</summary>
    public static QuickActionCatalog Default { get; } = new(Definitions());

    /// <inheritdoc/>
    public IReadOnlyList<QuickActionDefinition> All { get; }

    /// <inheritdoc/>
    public QuickActionDefinition? Find(string id) => id is not null && _byId.TryGetValue(id, out var found) ? found : null;

    private static IEnumerable<QuickActionDefinition> Definitions()
    {
        const QuickActionAvailability Available = QuickActionAvailability.Available;

        // The Assistant's own.
        yield return new(QuickActionIds.NewConversation, "New conversation", "Start asking something new", QuickActionCategory.Assistant, RiskLevel.ReadOnly, Available)
        {
            Keywords = ["new chat", "new conversation", "start over", "ask something"],
        };
        yield return new(QuickActionIds.ShowHistory, "Show history", "Look through past conversations", QuickActionCategory.Assistant, RiskLevel.ReadOnly, Available)
        {
            Keywords = ["history", "past conversations", "previous chats", "old chats"],
        };
        yield return new(QuickActionIds.OpenAssistantSettings, "Open Assistant settings", "Model, privacy, permissions and shortcuts", QuickActionCategory.Assistant, RiskLevel.ReadOnly, Available)
        {
            Keywords = ["settings", "preferences", "options", "assistant preferences"],
        };
        yield return new(QuickActionIds.TakeScreenshot, "Take screenshot", "Capture part of the screen to ask about it", QuickActionCategory.Assistant, RiskLevel.ReadOnly, Available)
        {
            Keywords = ["screenshot", "screen capture", "capture", "snip", "screen"],
            RequiredPermission = PermissionCapability.ScreenCapture,
        };
        yield return new(QuickActionIds.ClearClipboardHistory, "Clear clipboard history", "Forget everything the Assistant kept of what you copied", QuickActionCategory.Assistant, RiskLevel.SideEffect, Available)
        {
            Keywords = ["clipboard", "forget clipboard", "clear copied", "erase clipboard"],
            RequiredPermission = PermissionCapability.ClipboardHistory,
        };

        // Windows.
        yield return new(QuickActionIds.OpenWindowsSettings, "Open Windows settings", "Windows Settings", QuickActionCategory.Windows, RiskLevel.ReadOnly, Available)
        {
            Keywords = ["settings", "windows settings", "system settings", "control panel", "preferences"],
        };

        // Sound: changes a setting, and can be undone with the same action.
        yield return new(QuickActionIds.Mute, "Mute", "Turn the sound off", QuickActionCategory.Sound, RiskLevel.SideEffect, Available)
        {
            Keywords = ["silence", "sound off", "volume off", "quiet"],
        };
        yield return new(QuickActionIds.Unmute, "Unmute", "Turn the sound back on", QuickActionCategory.Sound, RiskLevel.SideEffect, Available)
        {
            Keywords = ["sound on", "volume on", "restore sound"],
        };
        yield return new(QuickActionIds.VolumeUp, "Volume up", "Raise the volume by 10%", QuickActionCategory.Sound, RiskLevel.SideEffect, Available)
        {
            Keywords = ["louder", "increase volume", "raise volume", "turn up"],
        };
        yield return new(QuickActionIds.VolumeDown, "Volume down", "Lower the volume by 10%", QuickActionCategory.Sound, RiskLevel.SideEffect, Available)
        {
            Keywords = ["quieter", "decrease volume", "lower volume", "turn down"],
        };
        yield return new(QuickActionIds.SetVolume, "Set volume", "Set the volume to a level, such as volume 30", QuickActionCategory.Sound, RiskLevel.SideEffect, Available)
        {
            Keywords = ["volume level", "change volume", "set volume"],
            Parameter = new QuickActionParameter("Volume", 0, 100),
        };

        // Common folders, opened in File Explorer.
        yield return Folder(QuickActionIds.OpenHome, "Open home folder", "Your user folder", ["home", "user folder", "profile"]);
        yield return Folder(QuickActionIds.OpenDesktop, "Open Desktop", "The Desktop folder", ["desktop folder"]);
        yield return Folder(QuickActionIds.OpenDocuments, "Open Documents", "The Documents folder", ["documents folder", "my documents"]);
        yield return Folder(QuickActionIds.OpenDownloads, "Open Downloads", "The Downloads folder", ["downloads folder", "downloaded files"]);
        yield return Folder(QuickActionIds.OpenPictures, "Open Pictures", "The Pictures folder", ["pictures folder", "photos folder", "images folder"]);
        yield return Folder(QuickActionIds.OpenMusic, "Open Music", "The Music folder", ["music folder", "songs folder"]);
        yield return Folder(QuickActionIds.OpenVideos, "Open Videos", "The Videos folder", ["videos folder", "movies folder"]);

        // The session.
        yield return new(QuickActionIds.LockPc, "Lock PC", "Lock this PC; sign back in to carry on", QuickActionCategory.Session, RiskLevel.SideEffect, Available)
        {
            Keywords = ["lock", "lock screen", "lock workstation", "lock computer"],
        };

        // Defined and not built yet: never listed, never run.
        yield return new(QuickActionIds.Sleep, "Sleep", "Put this PC to sleep", QuickActionCategory.Session, RiskLevel.SideEffect, QuickActionAvailability.Planned)
        {
            Note = "Not built: a sleeping PC can lose unsaved work in a program that does not handle it.",
        };

        // Refused by design: each can lose the user's work or data.
        yield return new(QuickActionIds.SignOut, "Sign out", "End this session", QuickActionCategory.Session, RiskLevel.Destructive, QuickActionAvailability.Refused)
        {
            Note = "Ends the session and closes programs with unsaved work.",
        };
        yield return new(QuickActionIds.Restart, "Restart", "Restart this PC", QuickActionCategory.Session, RiskLevel.Destructive, QuickActionAvailability.Refused)
        {
            Note = "Closes every program, with unsaved work.",
        };
        yield return new(QuickActionIds.ShutDown, "Shut down", "Turn this PC off", QuickActionCategory.Session, RiskLevel.Destructive, QuickActionAvailability.Refused)
        {
            Note = "Closes every program, with unsaved work.",
        };
        yield return new(QuickActionIds.EmptyRecycleBin, "Empty Recycle Bin", "Delete what is in the Recycle Bin for good", QuickActionCategory.Windows, RiskLevel.Destructive, QuickActionAvailability.Refused)
        {
            Note = "Deletes files for good (PROJECT_SPEC P8: no destructive actions).",
        };
    }

    private static QuickActionDefinition Folder(string id, string title, string description, string[] keywords) =>
        new(id, title, description, QuickActionCategory.Folder, RiskLevel.ReadOnly, QuickActionAvailability.Available)
        {
            Keywords = keywords,
            OpensFolder = true,
        };
}
