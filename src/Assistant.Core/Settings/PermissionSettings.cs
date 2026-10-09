namespace Assistant.Core.Settings;

/// <summary>
/// What the user allows the Assistant to use (PROJECT_SPEC §4.9): one switch for each <c>PermissionCapability</c>. A new
/// instance holds the defaults, which allow exactly the capabilities this build can do (<c>PermissionAvailability.Available</c>)
/// and nothing else, so a capability built by a later step starts off and the user turns it on themselves.
/// </summary>
/// <remarks>
/// A switch is only what the user asked for. Whether a capability may be used is decided by
/// <c>SettingsPermissionPolicy.Decide</c>, which also asks whether this build can do it, and everything that checks a
/// permission goes through <c>IPermissionPolicy</c> rather than reading a switch: a switch saved for
/// something the build cannot do, by hand or by a newer build, is kept as it is and has no effect. The extension methods
/// <c>IsOn</c> and <c>With</c> (<c>PermissionSettingsExtensions</c>) read and set a switch by its capability.
/// </remarks>
public sealed record PermissionSettings
{
    /// <summary>Reading files the user attaches to a question. On by default: attaching a file is the user's own act.</summary>
    public bool Files { get; init; } = true;

    /// <summary>
    /// Capturing a part of the screen with Visual Intelligence. On by default, like Files: the capture starts only when the user presses
    /// the shortcut and drags a rectangle, and stays in memory.
    /// </summary>
    public bool ScreenCapture { get; init; } = true;

    /// <summary>
    /// Reading the text selected in another app with the selected-text shortcut. On by default, like Files and Screen Capture: the
    /// selection is read only when the user presses the shortcut, and stays in memory.
    /// </summary>
    public bool SelectedText { get; init; } = true;

    /// <summary>
    /// Reading the selection of an app that does not share it by pressing Copy in that app (step 89). Off by default, unlike
    /// <see cref="SelectedText"/>: it sends a key press to the other app and borrows the clipboard, which can change that app's state.
    /// </summary>
    public bool SelectedTextByCopy { get; init; }

    /// <summary>
    /// Keeping a short history of the text the user copies, in memory only, for the bar's Clipboard category (step 95). Off by default: it
    /// is the one capability that notices something (a copy) without being asked, so the user turns it on themselves.
    /// </summary>
    public bool ClipboardHistory { get; init; }

    /// <summary>Reading the user's calendar.</summary>
    public bool Calendar { get; init; }

    /// <summary>Reading messages and contacts in messaging apps.</summary>
    public bool Messaging { get; init; }

    /// <summary>Searching the web or finding images online.</summary>
    public bool ExternalSearch { get; init; }

    /// <summary>Deleting or overwriting data.</summary>
    public bool DestructiveActions { get; init; }

    /// <summary>
    /// Which of the switches that are on are to ask the user before each use instead of allowing every use (step 119). A switch that is off stays off
    /// whatever is here, and one the Assistant can only allow or refuse (<c>PermissionDefinition.SupportsAskEveryTime</c>) ignores it by refusing, so a
    /// hand-edited file cannot turn a use the user wanted to be asked about into one that is simply allowed. Nothing is asked by default.
    /// </summary>
    public PermissionAskSettings Ask { get; init; } = new();

    /// <summary>
    /// The actions the user chose "Always allow" for when asked (step 115): each a tool's name and a fingerprint of what the tool was then
    /// (<c>StandingApprovals</c>). A call of one of them is made without the question. Only what the question offered it for can be here to any effect: a
    /// message is asked about every time whatever this list holds, and a tool that has changed since is asked about again. Empty by default.
    /// </summary>
    public IReadOnlyList<string> AlwaysAllowed
    {
        get => _alwaysAllowed;
        init => _alwaysAllowed = value as Entries ?? new Entries([.. value ?? []]);
    }

    private readonly IReadOnlyList<string> _alwaysAllowed = new Entries([]);

    // A list that equals another with the same entries in the same order: two settings that hold the same are then the same settings, as they are for
    // every switch, whichever list each was read into.
    private sealed class Entries(IList<string> entries) : System.Collections.ObjectModel.ReadOnlyCollection<string>(entries)
    {
        public override bool Equals(object? obj) => obj is IEnumerable<string> other && this.SequenceEqual(other, StringComparer.Ordinal);

        public override int GetHashCode() => Count;
    }
}

/// <summary>
/// For each capability that has the choice, whether its switch asks the user before each use (<see cref="PermissionSettings.Ask"/>). A member that is
/// <see langword="true"/> means "when this is on, ask every time"; it does nothing while the capability's own switch is off. Read and written by
/// <c>PermissionSettingsExtensions.ModeOf</c> and <c>WithMode</c>, never directly.
/// </summary>
public sealed record PermissionAskSettings
{
    /// <summary>Ask before each capture of the screen.</summary>
    public bool ScreenCapture { get; init; }

    /// <summary>Ask before each reading of a selection in another app.</summary>
    public bool SelectedText { get; init; }

    /// <summary>Ask before pressing Copy in another app.</summary>
    public bool SelectedTextByCopy { get; init; }

    /// <summary>Ask before each reading of a calendar.</summary>
    public bool Calendar { get; init; }

    /// <summary>Ask before each look into a messaging app.</summary>
    public bool Messaging { get; init; }

    /// <summary>Ask before each look-up or search that sends something off this PC.</summary>
    public bool ExternalSearch { get; init; }
}
