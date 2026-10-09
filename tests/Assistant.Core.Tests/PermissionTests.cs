using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What the user can allow the Assistant to use (PROJECT_SPEC §4.9): the catalog, the switches and the decision.</summary>
public sealed class PermissionTests
{
    private static readonly PermissionCapability[] Every = Enum.GetValues<PermissionCapability>();

    [Fact]
    public void TheCatalogListsEveryCapabilityOnceInTheOrderThePageShowsThem()
    {
        Assert.Equal(
            [
                PermissionCapability.Files, PermissionCapability.ScreenCapture, PermissionCapability.SelectedText,
                PermissionCapability.SelectedTextByCopy, PermissionCapability.ClipboardHistory, PermissionCapability.Calendar,
                PermissionCapability.Messaging, PermissionCapability.ExternalSearch, PermissionCapability.DestructiveActions,
            ],
            PermissionCatalog.All.Select(definition => definition.Capability));
        Assert.Equal(Every.Order(), PermissionCatalog.All.Select(definition => definition.Capability).Order());
    }

    [Fact]
    public void EveryCapabilityHasANameAndASentenceForTheUser()
    {
        foreach (var definition in PermissionCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Title));
            Assert.False(string.IsNullOrWhiteSpace(definition.Summary));
            Assert.Same(definition, PermissionCatalog.Get(definition.Capability));
        }

        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(definition => definition.Title).Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => PermissionCatalog.Get((PermissionCapability)99));
    }

    [Fact]
    public void OnlyWhatTheAssistantCanDoTodayIsAvailable()
    {
        // The step that builds a capability changes it in the catalog and here, on purpose: this is the list of what works.
        Assert.Equal(
            [
                PermissionCapability.Files, PermissionCapability.ScreenCapture, PermissionCapability.SelectedText,
                PermissionCapability.SelectedTextByCopy, PermissionCapability.ClipboardHistory, PermissionCapability.Calendar,
                PermissionCapability.Messaging, PermissionCapability.ExternalSearch,
            ],
            PermissionCatalog.All.Where(definition => definition.IsAvailable).Select(definition => definition.Capability));
        Assert.Equal(PermissionAvailability.Available, PermissionCatalog.Get(PermissionCapability.SelectedText).Availability);
        Assert.Equal(PermissionAvailability.Available, PermissionCatalog.Get(PermissionCapability.Calendar).Availability);
        Assert.Equal(PermissionAvailability.Available, PermissionCatalog.Get(PermissionCapability.Messaging).Availability);
        Assert.Equal(PermissionAvailability.AlwaysOff, PermissionCatalog.Get(PermissionCapability.DestructiveActions).Availability);
    }

    [Fact]
    public void ByDefaultExactlyTheCapabilitiesTheAssistantCanDoThatStayOnThisPcAreOn()
    {
        var defaults = new PermissionSettings();

        foreach (var definition in PermissionCatalog.All)
        {
            var on = definition.IsAvailable && !definition.LeavesThisPc && !definition.ActsOnOtherApps;
            Assert.Equal(on, defaults.IsOn(definition.Capability));
            Assert.Equal(on, SettingsPermissionPolicy.Decide(defaults, definition.Capability).IsAllowed);
        }

        // The one that sends something away starts off: the user turns it on themselves.
        Assert.Equal([PermissionCapability.ExternalSearch], PermissionCatalog.All.Where(definition => definition.LeavesThisPc).Select(definition => definition.Capability));
        Assert.True(PermissionCatalog.Get(PermissionCapability.ExternalSearch).IsAvailable);
        Assert.False(defaults.ExternalSearch);

        // So do the ones that press a key in another app and borrow the clipboard (step 89), and that keep a history of what is copied
        // (step 95): available, and off until the user allows them.
        Assert.Equal(
            [PermissionCapability.SelectedTextByCopy, PermissionCapability.ClipboardHistory, PermissionCapability.Calendar, PermissionCapability.Messaging],
            PermissionCatalog.All.Where(definition => definition.ActsOnOtherApps).Select(definition => definition.Capability));
        Assert.True(PermissionCatalog.Get(PermissionCapability.SelectedTextByCopy).IsAvailable);
        Assert.False(defaults.SelectedTextByCopy);
        Assert.False(SettingsPermissionPolicy.Decide(defaults, PermissionCapability.SelectedTextByCopy).IsAllowed);
        Assert.True(SettingsPermissionPolicy.Decide(defaults with { SelectedTextByCopy = true }, PermissionCapability.SelectedTextByCopy).IsAllowed);
        Assert.True(defaults.SelectedText);
        Assert.True(PermissionCatalog.Get(PermissionCapability.ClipboardHistory).IsAvailable);
        Assert.False(defaults.ClipboardHistory);
        Assert.False(SettingsPermissionPolicy.Decide(defaults, PermissionCapability.ClipboardHistory).IsAllowed);
        Assert.True(SettingsPermissionPolicy.Decide(defaults with { ClipboardHistory = true }, PermissionCapability.ClipboardHistory).IsAllowed);
    }

    [Fact]
    public void ASwitchIsReadAndSetForEveryCapabilityAndOnlyThatOne()
    {
        foreach (var capability in Every)
        {
            var on = new PermissionSettings().With(capability, true);
            var off = new PermissionSettings().With(capability, false);

            Assert.True(on.IsOn(capability));
            Assert.False(off.IsOn(capability));
            foreach (var other in Every.Where(other => other != capability))
            {
                Assert.Equal(new PermissionSettings().IsOn(other), on.IsOn(other));
                Assert.Equal(new PermissionSettings().IsOn(other), off.IsOn(other));
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSettings().IsOn((PermissionCapability)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSettings().With((PermissionCapability)99, true));
    }

    [Fact]
    public void ACapabilityIsAllowedOnlyWhenTheAssistantCanDoItAndTheSwitchIsOn()
    {
        Assert.Equal(
            PermissionDecisionReason.Granted,
            SettingsPermissionPolicy.Decide(new PermissionSettings { Files = true }, PermissionCapability.Files).Reason);
        Assert.Equal(
            PermissionDecisionReason.TurnedOff,
            SettingsPermissionPolicy.Decide(new PermissionSettings { Files = false }, PermissionCapability.Files).Reason);

        // Every switch on, as a hand-edited file could have it: what the Assistant cannot do stays refused, and the reason
        // says it is the build and not the user's choice.
        var everythingOn = Every.Aggregate(new PermissionSettings(), (settings, capability) => settings.With(capability, true));
        foreach (var definition in PermissionCatalog.All.Where(definition => !definition.IsAvailable))
        {
            var decision = SettingsPermissionPolicy.Decide(everythingOn, definition.Capability);
            Assert.False(decision.IsAllowed);
            Assert.Equal(PermissionDecisionReason.NotAvailable, decision.Reason);
            Assert.Equal(definition.Capability, decision.Capability);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsPermissionPolicy.Decide(everythingOn, (PermissionCapability)99));
    }

    [Fact]
    public async Task ThePolicyReadsTheSavedSettingsEachTimeItIsAsked()
    {
        var settings = new FixedSettings();
        var policy = new SettingsPermissionPolicy(settings);

        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);

        settings.Current = settings.Current with { Permissions = settings.Current.Permissions with { Files = false } };
        var off = await policy.CheckAsync(PermissionCapability.Files);
        Assert.False(off.IsAllowed);
        Assert.Equal(PermissionDecisionReason.TurnedOff, off.Reason);

        settings.Current = settings.Current with { Permissions = settings.Current.Permissions with { Files = true } };
        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);
    }

    [Fact]
    public async Task ThePolicyRefusesWhatTheAssistantCannotDoWhateverTheSettingsSay()
    {
        var everythingOn = Every.Aggregate(new PermissionSettings(), (settings, capability) => settings.With(capability, true));
        var policy = new SettingsPermissionPolicy(new FixedSettings(new AppSettings { Permissions = everythingOn }));

        foreach (var capability in Every.Where(capability => !PermissionCatalog.Get(capability).IsAvailable))
        {
            Assert.False((await policy.CheckAsync(capability)).IsAllowed);
        }

        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);
        Assert.True((await policy.CheckAsync(PermissionCapability.ScreenCapture)).IsAllowed);
        Assert.True((await policy.CheckAsync(PermissionCapability.ExternalSearch)).IsAllowed);
        Assert.Throws<ArgumentNullException>(() => new SettingsPermissionPolicy(null!));
    }

    [Fact]
    public void ThePermissionsAreAnOrdinarySettingsSectionSoTheDefaultsAndTheValidatorCoverThem()
    {
        Assert.Equal(new PermissionSettings(), new AppSettings().Permissions);
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
        Assert.Empty(SettingsValidator.Validate(new AppSettings
        {
            Permissions = Every.Aggregate(new PermissionSettings(), (settings, capability) => settings.With(capability, true)),
        }));

        var sanitized = SettingsValidator.Sanitize(new AppSettings { Permissions = null! }, out var repaired);

        Assert.Equal(new PermissionSettings(), sanitized.Permissions);
        Assert.Equal("Permissions", Assert.Single(repaired).Setting);
    }

    [Fact]
    public void ADecisionHoldsNothingButTheCapabilityAndWhy()
    {
        var properties = typeof(PermissionDecision).GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(["Capability", "CouldBeAllowed", "IsAllowed", "NeedsAsking", "Reason"], properties);
    }
}
