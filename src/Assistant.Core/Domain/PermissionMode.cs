namespace Assistant.Core.Domain;

/// <summary>
/// What the user has chosen for a <see cref="PermissionCapability"/> (PROJECT_SPEC §4.9, step 119): refuse it, be asked each time it would be used, or
/// let the Assistant use it whenever the user's own request needs it. The mode is only the user's choice: whether the Assistant may use the capability
/// is decided by <c>SettingsPermissionPolicy</c>, which also asks whether this build can do it at all.
/// </summary>
public enum PermissionMode
{
    /// <summary>The Assistant may not use it.</summary>
    Off = 0,

    /// <summary>
    /// The Assistant may use it once the user has said yes to that one use, each time. A use nobody could ask about is a no. Only a capability whose uses
    /// are single events the user can be asked about has this mode (<c>PermissionDefinition.SupportsAskEveryTime</c>).
    /// </summary>
    AskEveryTime = 1,

    /// <summary>The Assistant may use it when the user's own request needs it, without asking again.</summary>
    Allowed = 2,
}
