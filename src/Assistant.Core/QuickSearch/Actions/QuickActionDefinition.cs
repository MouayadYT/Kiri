using Assistant.Core.Domain;

namespace Assistant.Core.QuickSearch.Actions;

/// <summary>What a quick action is about, which is how related actions are told apart in metadata.</summary>
public enum QuickActionCategory
{
    /// <summary>The Assistant itself: a new conversation, its history, its settings.</summary>
    Assistant = 0,

    /// <summary>Windows: its settings.</summary>
    Windows = 1,

    /// <summary>Sound: the volume.</summary>
    Sound = 2,

    /// <summary>A common folder, opened in File Explorer.</summary>
    Folder = 3,

    /// <summary>The user's session: locking the PC.</summary>
    Session = 4,
}

/// <summary>Whether the app lists and runs a quick action.</summary>
public enum QuickActionAvailability
{
    /// <summary>It is built, safe, and wired: it is listed and runs when the user chooses it.</summary>
    Available = 0,

    /// <summary>It is defined and not built yet: it is never listed and never run.</summary>
    Planned = 1,

    /// <summary>
    /// The app will not do it, by design (it can lose the user's work or data, or end their session without saving it): it is never
    /// listed and never run, whatever asks.
    /// </summary>
    Refused = 2,
}

/// <summary>A whole number that an action is given, such as a volume level.</summary>
/// <param name="Name">What it is, for the user ("Volume").</param>
/// <param name="Minimum">The least it may be.</param>
/// <param name="Maximum">The most it may be.</param>
public sealed record QuickActionParameter(string Name, int Minimum, int Maximum);

/// <summary>
/// What is known about one quick action before it is wired (PROJECT_SPEC §4.1): what it is called and does, how safe it is, whether it
/// is built, what it may need. Actions are deterministic: the app runs them when the user chooses one, and they are not tools, so
/// the model cannot run them (PROJECT_SPEC §4.8). Risk and availability are fixed in code; nothing the user types or a file says can
/// change them.
/// </summary>
/// <param name="Id">A stable id, never reused for another action (<c>system.lock</c>).</param>
/// <param name="Title">What it does, in words, as it is listed ("Lock PC").</param>
/// <param name="Description">A line that says more, which is the result's second line.</param>
/// <param name="Category">What it is about.</param>
/// <param name="Risk">
/// <see cref="RiskLevel.ReadOnly"/> for one that only opens something, <see cref="RiskLevel.SideEffect"/> for one that changes
/// a setting or the state of the PC and can be undone, and <see cref="RiskLevel.Destructive"/> for one that cannot: those are
/// always <see cref="QuickActionAvailability.Refused"/>.
/// </param>
/// <param name="Availability">Whether it is listed and run.</param>
public sealed record QuickActionDefinition(
    string Id, string Title, string Description, QuickActionCategory Category, RiskLevel Risk, QuickActionAvailability Availability)
{
    /// <summary>Other words the action is found by ("history" for "Show history").</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>The number the action needs, when it is given one with the words typed ("volume 30"); <see langword="null"/> for none.</summary>
    public QuickActionParameter? Parameter { get; init; }

    /// <summary>The permission the user must have turned on for the action to be listed, or <see langword="null"/> for none.</summary>
    public PermissionCapability? RequiredPermission { get; init; }

    /// <summary>Whether the action is shown with a folder's icon.</summary>
    public bool OpensFolder { get; init; }

    /// <summary>Why a planned or refused action is not wired, for whoever reads the catalog.</summary>
    public string? Note { get; init; }

    /// <summary>Whether the app lists and runs it: it is built and does not destroy anything.</summary>
    public bool IsSafeToRun => Availability == QuickActionAvailability.Available && Risk != RiskLevel.Destructive;
}
