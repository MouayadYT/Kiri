using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>
/// Says whether the user allows the Assistant a kind of access at all (PROJECT_SPEC §4.9), such as reading files or capturing
/// the screen. Anything that reads the user's files, screen or selection asks here first, so what the Permissions page shows
/// is what holds. It is a different question from <see cref="IPermissionService"/>, which asks the user to confirm one tool
/// call: a capability that is off never gets as far as asking.
/// </summary>
/// <remarks>
/// A capability is allowed only when this build can do it (<c>PermissionCatalog</c>) and the user has not turned it
/// off, so a settings file that is edited by hand cannot allow what the Assistant refuses to do.
/// </remarks>
public interface IPermissionPolicy
{
    /// <summary>Decides whether <paramref name="capability"/> may be used, by the settings as they are saved now.</summary>
    Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default);
}
