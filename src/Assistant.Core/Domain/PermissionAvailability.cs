namespace Assistant.Core.Domain;

/// <summary>
/// Whether this build of the Assistant can do what a <see cref="PermissionCapability"/> covers at all. It is fixed in code,
/// like a tool's risk level (PROJECT_SPEC §4.8): nothing the user saves and nothing a model says can make a capability
/// available. Only an available one can be allowed; every other is shown, and stays off.
/// </summary>
public enum PermissionAvailability
{
    /// <summary>The Assistant can do it now, so the user chooses whether it may.</summary>
    Available = 0,

    /// <summary>A later step of this version builds it. Until then it cannot be allowed.</summary>
    Planned = 1,

    /// <summary>
    /// Left out of this version (PROJECT_SPEC §7.2, N9 and N10), so it cannot be allowed however the settings are edited.
    /// </summary>
    NotInThisVersion = 2,

    /// <summary>
    /// Refused by design in this version (PROJECT_SPEC §3.1 P8, §4.8: a destructive tool is always rejected), so it stays
    /// off and the user cannot change that.
    /// </summary>
    AlwaysOff = 3,
}
