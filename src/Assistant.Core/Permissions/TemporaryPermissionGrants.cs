using Assistant.Core.Domain;

namespace Assistant.Core.Permissions;

/// <summary>
/// What the permission policy is told to allow for now, whatever the user's switches and this build's catalog say (PROJECT_SPEC §4.9, step 116). It exists so that a demonstration whose
/// parts are all made up (the calendar and the messaging app of <c>demo reminder</c>, which read no real calendar and reach no one) can be tried although Calendar and Messaging are not in
/// this version, without a switch being turned on in Settings and without the policy being wrapped in anything: <see cref="SettingsPermissionPolicy"/> asks it first, and an empty set
/// changes nothing. Only code that is given it can grant, a grant lasts until it is taken back or the app ends, and it is never saved.
/// </summary>
public interface ITemporaryPermissionGrants
{
    /// <summary>Whether <paramref name="capability"/> is allowed for now.</summary>
    bool IsGranted(PermissionCapability capability);
}

/// <summary>The grants a demonstration gives and takes back (<see cref="ITemporaryPermissionGrants"/>).</summary>
public sealed class TemporaryPermissionGrants : ITemporaryPermissionGrants
{
    private readonly object _gate = new();
    private readonly HashSet<PermissionCapability> _granted = [];

    /// <inheritdoc/>
    public bool IsGranted(PermissionCapability capability)
    {
        lock (_gate)
        {
            return _granted.Contains(capability);
        }
    }

    /// <summary>Allows <paramref name="capabilities"/> until <see cref="Revoke"/> takes them back.</summary>
    public void Grant(params PermissionCapability[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_gate)
        {
            _granted.UnionWith(capabilities);
        }
    }

    /// <summary>Takes back what <see cref="Grant"/> allowed of <paramref name="capabilities"/>.</summary>
    public void Revoke(params PermissionCapability[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_gate)
        {
            _granted.ExceptWith(capabilities);
        }
    }
}
