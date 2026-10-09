using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Permissions;

/// <summary>
/// The permission policy: a capability is allowed when this build can do it (<see cref="PermissionCatalog"/>) and the
/// user's saved choice for it is on; one set to ask every time is allowed only for the work the user has just said yes to (<see cref="PermissionApprovals"/>). It reads the settings each time it is asked, which costs nothing since the settings
/// service keeps them in memory, so a switch turned off takes effect for the very next request.
/// </summary>
public sealed class SettingsPermissionPolicy : IPermissionPolicy
{
    private readonly ISettingsService _settings;
    private readonly ITemporaryPermissionGrants? _grants;

    /// <summary>Creates the policy over the settings the switches are saved in.</summary>
    /// <param name="settings">Where the switches are saved.</param>
    /// <param name="grants">What a demonstration made of made-up parts has allowed for now (step 116); without it, or while it holds nothing, only the settings decide.</param>
    public SettingsPermissionPolicy(ISettingsService settings, ITemporaryPermissionGrants? grants = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _grants = grants;
    }

    /// <summary>
    /// Decides whether <paramref name="capability"/> may be used under <paramref name="permissions"/>. A capability this
    /// build cannot do is refused before its switch is looked at.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability.</exception>
    public static PermissionDecision Decide(PermissionSettings permissions, PermissionCapability capability)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var definition = PermissionCatalog.Get(capability);
        if (!definition.IsAvailable)
        {
            return new PermissionDecision(capability, PermissionDecisionReason.NotAvailable);
        }

        return new PermissionDecision(
            capability,
            permissions.ModeOf(capability) switch
            {
                PermissionMode.Allowed => PermissionDecisionReason.Granted,
                PermissionMode.AskEveryTime => PermissionDecisionReason.AskEveryTime,
                _ => PermissionDecisionReason.TurnedOff,
            });
    }

    /// <inheritdoc/>
    public async Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
    {
        if (_grants?.IsGranted(capability) == true)
        {
            return new PermissionDecision(capability, PermissionDecisionReason.Granted);
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var decision = Decide(settings.Permissions, capability);

        // A capability set to ask each time is allowed for the work the user has just said yes to, and for nothing else: an approval cannot raise one that is off.
        return decision.NeedsAsking && PermissionApprovals.IsApproved(capability)
            ? new PermissionDecision(capability, PermissionDecisionReason.Granted)
            : decision;
    }
}
