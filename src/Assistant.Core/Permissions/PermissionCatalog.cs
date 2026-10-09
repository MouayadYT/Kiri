using Assistant.Core.Domain;

namespace Assistant.Core.Permissions;

/// <summary>What one <see cref="PermissionCapability"/> is, in words the user reads, and whether it can be allowed now.</summary>
/// <param name="Capability">The capability.</param>
/// <param name="Title">Its name, as the Permissions page shows it.</param>
/// <param name="Summary">A sentence on what allowing it lets the Assistant do.</param>
/// <param name="Availability">Whether this build can do it at all.</param>
public sealed record PermissionDefinition(
    PermissionCapability Capability, string Title, string Summary, PermissionAvailability Availability)
{
    /// <summary>Whether the user can allow it: the Assistant can do it now.</summary>
    public bool IsAvailable => Availability == PermissionAvailability.Available;

    /// <summary>
    /// Whether using it sends something off this PC (PROJECT_SPEC §3.4). What does starts off, whatever else the build can do: the user
    /// turns it on themselves, and Local Only mode must be off as well.
    /// </summary>
    public bool LeavesThisPc { get; init; }

    /// <summary>
    /// Whether using it acts on another application or on something of the user's that is not the Assistant's own (a key press sent to
    /// the app in front, the clipboard), which can change its state. Like <see cref="LeavesThisPc"/>, it makes the capability start off.
    /// </summary>
    public bool ActsOnOtherApps { get; init; }

    /// <summary>
    /// Whether the user can choose to be asked before each use (<see cref="PermissionMode.AskEveryTime"/>) instead of allowing every use (step 119). It is
    /// true for a capability whose uses are single events the user can be asked about, each starting with something they did (a shortcut, a request that
    /// reads their calendar); it is false for one that works continuously or inside what the user types (looking up files as they type, the clipboard
    /// history), where a question for each use could not be asked, and for one that is always off.
    /// </summary>
    public bool SupportsAskEveryTime { get; init; }

    /// <summary>The question put to the user when a use of the capability is to be confirmed, such as "Let the Assistant capture part of your screen?"; empty when it is never asked.</summary>
    public string AskQuestion { get; init; } = string.Empty;
}

/// <summary>
/// Every capability the user can allow or refuse (PROJECT_SPEC §4.9), in the order they are listed, and what this build can
/// do of each. It is the one place that says which are <see cref="PermissionAvailability.Available"/>: the step that builds
/// a capability changes it here, and the Permissions page, the saved defaults and every check follow.
/// </summary>
public static class PermissionCatalog
{
    private static readonly PermissionDefinition[] Definitions =
    [
        new(
            PermissionCapability.Files, "Files",
            "Find your files and read the pictures you attach.",
            PermissionAvailability.Available),
        new(
            PermissionCapability.ScreenCapture, "Screen Capture",
            "Capture the part of the screen you choose.",
            PermissionAvailability.Available)
        {
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant capture part of your screen?",
        },
        new(
            PermissionCapability.SelectedText, "Selected Text",
            "Read the text you have selected.",
            PermissionAvailability.Available)
        {
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant read the text you selected?",
        },
        new(
            PermissionCapability.SelectedTextByCopy, "Selected Text by Copy",
            "Press Copy to read your selection.",
            PermissionAvailability.Available)
        {
            ActsOnOtherApps = true,
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant press Copy in the app in front to read your selection?",
        },
        new(
            PermissionCapability.ClipboardHistory, "Clipboard History",
            "Keep what you copy, in memory only.",
            PermissionAvailability.Available)
        {
            ActsOnOtherApps = true,
        },
        new(
            PermissionCapability.Calendar, "Calendar",
            "Read your connected calendar.",
            PermissionAvailability.Available)
        {
            // It reads something of the user's that another app keeps, so it starts off.
            ActsOnOtherApps = true,
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant read your calendar?",
        },
        new(
            PermissionCapability.Messaging, "Messaging",
            "Send messages through a connected app.",
            PermissionAvailability.Available)
        {
            // It reaches into another app and can speak for the user, so it starts off.
            ActsOnOtherApps = true,
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant look in your messaging app?",
        },
        new(
            PermissionCapability.ExternalSearch, "External Web and Image Search",
            "Web search and integrations, when Local Only is off.",
            PermissionAvailability.Available)
        {
            LeavesThisPc = true,
            SupportsAskEveryTime = true,
            AskQuestion = "Let the Assistant look this up on the web?",
        },
        new(
            PermissionCapability.DestructiveActions, "Destructive Actions",
            "Delete or overwrite things.",
            PermissionAvailability.AlwaysOff),
    ];

    /// <summary>Every capability, in the order the Permissions page lists them.</summary>
    public static IReadOnlyList<PermissionDefinition> All => Definitions;

    /// <summary>The definition of <paramref name="capability"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability.</exception>
    public static PermissionDefinition Get(PermissionCapability capability) =>
        Definitions.FirstOrDefault(definition => definition.Capability == capability)
        ?? throw new ArgumentOutOfRangeException(nameof(capability), capability, "Not a known capability.");
}
