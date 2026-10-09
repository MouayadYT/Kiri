using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// What a demonstration of made-up parts may allow for now (PROJECT_SPEC §4.9, step 116): nothing until it grants, only what it names, and the settings are the user's own again
/// when it takes the grant back.
/// </summary>
public sealed class TemporaryPermissionGrantsTests
{
    [Fact]
    public void NothingIsGrantedUntilSomethingIs()
    {
        var grants = new TemporaryPermissionGrants();

        Assert.All(Enum.GetValues<PermissionCapability>(), capability => Assert.False(grants.IsGranted(capability)));
    }

    [Fact]
    public void OnlyWhatIsNamedIsGrantedAndRevokingTakesItBack()
    {
        var grants = new TemporaryPermissionGrants();

        grants.Grant(PermissionCapability.Calendar, PermissionCapability.Messaging);

        Assert.True(grants.IsGranted(PermissionCapability.Calendar));
        Assert.True(grants.IsGranted(PermissionCapability.Messaging));
        Assert.False(grants.IsGranted(PermissionCapability.Files));
        grants.Revoke(PermissionCapability.Messaging);
        Assert.True(grants.IsGranted(PermissionCapability.Calendar));
        Assert.False(grants.IsGranted(PermissionCapability.Messaging));
    }

    [Fact]
    public void GrantingTwiceAndRevokingWhatWasNeverGrantedAreHarmless()
    {
        var grants = new TemporaryPermissionGrants();

        grants.Grant(PermissionCapability.Calendar);
        grants.Grant(PermissionCapability.Calendar);
        grants.Revoke(PermissionCapability.Files);
        grants.Revoke(PermissionCapability.Calendar);

        Assert.False(grants.IsGranted(PermissionCapability.Calendar));
        Assert.Throws<ArgumentNullException>(() => grants.Grant(null!));
        Assert.Throws<ArgumentNullException>(() => grants.Revoke(null!));
    }

    [Fact]
    public async Task ThePolicyAllowsWhatIsGrantedWhateverTheSettingsAndTheCatalogSayAndNothingElse()
    {
        var grants = new TemporaryPermissionGrants();
        var policy = new SettingsPermissionPolicy(new FixedSettings(), grants);

        var before = await policy.CheckAsync(PermissionCapability.Messaging);
        grants.Grant(PermissionCapability.Messaging);
        var during = await policy.CheckAsync(PermissionCapability.Messaging);
        var other = await policy.CheckAsync(PermissionCapability.Calendar);
        grants.Revoke(PermissionCapability.Messaging);
        var after = await policy.CheckAsync(PermissionCapability.Messaging);

        Assert.False(before.IsAllowed);
        Assert.Equal(PermissionDecisionReason.TurnedOff, before.Reason);
        Assert.True(during.IsAllowed);
        Assert.Equal(PermissionDecisionReason.Granted, during.Reason);
        Assert.False(other.IsAllowed);
        Assert.False(after.IsAllowed);
        Assert.Equal(PermissionDecisionReason.TurnedOff, after.Reason);
    }

    [Fact]
    public async Task APolicyMadeWithoutGrantsDecidesFromTheSettingsAlone()
    {
        var policy = new SettingsPermissionPolicy(new FixedSettings());

        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);
        Assert.False((await policy.CheckAsync(PermissionCapability.Messaging)).IsAllowed);
    }

    [Fact]
    public async Task AGrantDoesNotTurnAnythingOnInTheSavedSettings()
    {
        var settings = new FixedSettings();
        var grants = new TemporaryPermissionGrants();
        grants.Grant(PermissionCapability.Files);
        var policy = new SettingsPermissionPolicy(settings, grants);

        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);

        Assert.Equal(new AppSettings().Permissions, settings.Current.Permissions);
    }
}
