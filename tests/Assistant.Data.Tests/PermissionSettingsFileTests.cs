using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>How the permission switches (PROJECT_SPEC §4.9) are kept in the settings file.</summary>
public sealed class PermissionSettingsFileTests : IDisposable
{
    private readonly SettingsFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task EverySwitchIsKeptAcrossARestartAndTheFileNamesEachOneInPlainWords()
    {
        var saved = new PermissionSettings
        {
            Files = false, ScreenCapture = false, SelectedText = false, Calendar = true, Messaging = true, ExternalSearch = true,
            DestructiveActions = true,
        };
        await _folder.CreateService().SaveAsync(new AppSettings { Permissions = saved });

        var restarted = _folder.CreateService();
        var loaded = await restarted.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, restarted.Outcome);
        Assert.Equal(saved, loaded.Permissions);
        var text = await File.ReadAllTextAsync(_folder.FilePath);
        foreach (var name in new[] { "files", "screenCapture", "selectedText", "calendar", "messaging", "externalSearch", "destructiveActions" })
        {
            Assert.Contains($"\"{name}\":", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASettingsFileFromBeforeThePermissionsPageStillLoadsWithTheDefaultsAndNothingIsReportedAsRepaired()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "privacy": { "historyEnabled": false } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, service.Outcome);
        Assert.Equal(new PermissionSettings(), settings.Permissions);
        Assert.False(settings.Privacy.HistoryEnabled);
    }

    [Fact]
    public async Task ASwitchThatIsNotTrueOrFalseGetsItsDefaultAndTheOthersAreKept()
    {
        // "no" is not a switch position: Files goes back to its default, which is on. The rest of the section is kept.
        _folder.WriteFile("""{ "schemaVersion": 1, "permissions": { "files": "no", "screenCapture": true, "selectedText": 3 } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
        Assert.True(settings.Permissions.Files);
        Assert.True(settings.Permissions.ScreenCapture);
        Assert.True(settings.Permissions.SelectedText);
    }

    [Fact]
    public async Task APermissionsSectionThatIsNullOrNotAnObjectGetsTheDefaults()
    {
        foreach (var section in new[] { "null", "true", "[]", "\"all\"" })
        {
            var folder = new SettingsFolder();
            try
            {
                folder.WriteFile($$"""{ "schemaVersion": 1, "permissions": {{section}}, "privacy": { "historyEnabled": false } }""");
                var service = folder.CreateService();

                var settings = await service.LoadAsync();

                Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
                Assert.Equal(new PermissionSettings(), settings.Permissions);
                Assert.False(settings.Privacy.HistoryEnabled);
            }
            finally
            {
                folder.Dispose();
            }
        }
    }

    [Fact]
    public async Task ASwitchSavedForSomethingTheAssistantCannotDoIsKeptButNeverAllowsIt()
    {
        // Edited by hand, or saved by a newer build that could do it: the file keeps it, and this build still refuses.
        _folder.WriteFile("""{ "schemaVersion": 1, "permissions": { "destructiveActions": true, "calendar": true } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, service.Outcome);
        Assert.True(settings.Permissions.DestructiveActions);
        var policy = new SettingsPermissionPolicy(service);
        var destructive = await policy.CheckAsync(PermissionCapability.DestructiveActions);
        Assert.False(destructive.IsAllowed);
        Assert.Equal(PermissionDecisionReason.NotAvailable, destructive.Reason);

        // Calendar can be allowed since step 119, so what the file says for it holds.
        Assert.True((await policy.CheckAsync(PermissionCapability.Calendar)).IsAllowed);
    }

    [Fact]
    public async Task TurningFilesOffIsSeenByTheVeryNextCheck()
    {
        var service = _folder.CreateService();
        var policy = new SettingsPermissionPolicy(service);
        Assert.True((await policy.CheckAsync(PermissionCapability.Files)).IsAllowed);

        await service.UpdateAsync(settings => settings with { Permissions = settings.Permissions with { Files = false } });

        var decision = await policy.CheckAsync(PermissionCapability.Files);
        Assert.False(decision.IsAllowed);
        Assert.Equal(PermissionDecisionReason.TurnedOff, decision.Reason);
        // ...and it is still off after a restart.
        Assert.False((await new SettingsPermissionPolicy(_folder.CreateService()).CheckAsync(PermissionCapability.Files)).IsAllowed);
    }

    [Fact]
    public async Task AChoiceToBeAskedEachTimeIsKeptAcrossARestartAndTheFileSaysItInPlainWords()
    {
        var saved = new PermissionSettings().WithMode(PermissionCapability.Calendar, PermissionMode.AskEveryTime)
            .WithMode(PermissionCapability.ScreenCapture, PermissionMode.AskEveryTime).WithMode(PermissionCapability.Messaging, PermissionMode.Allowed);
        await _folder.CreateService().SaveAsync(new AppSettings { Permissions = saved });

        var loaded = (await _folder.CreateService().LoadAsync()).Permissions;

        Assert.Equal(saved, loaded);
        Assert.Equal(PermissionMode.AskEveryTime, loaded.ModeOf(PermissionCapability.Calendar));
        Assert.Equal(PermissionMode.AskEveryTime, loaded.ModeOf(PermissionCapability.ScreenCapture));
        Assert.Equal(PermissionMode.Allowed, loaded.ModeOf(PermissionCapability.Messaging));
        var text = await File.ReadAllTextAsync(_folder.FilePath);
        Assert.Contains("\"ask\":", text, StringComparison.Ordinal);
        Assert.Contains("\"screenCapture\": true", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingsFileFromBeforeAskEveryTimeLoadsAsAllowedWhereItWasOnAndNothingIsRepaired()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "permissions": { "files": true, "screenCapture": true, "calendar": true } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, service.Outcome);
        Assert.Equal(PermissionMode.Allowed, settings.Permissions.ModeOf(PermissionCapability.ScreenCapture));
        Assert.Equal(PermissionMode.Allowed, settings.Permissions.ModeOf(PermissionCapability.Calendar));
        Assert.Equal(new PermissionAskSettings(), settings.Permissions.Ask);
    }

    [Fact]
    public async Task AnUnreadableAskValueGetsItsDefaultAndTheRestOfTheSectionIsKept()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "permissions": { "calendar": true, "ask": { "calendar": "yes", "messaging": true } } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
        Assert.Equal(PermissionMode.Allowed, settings.Permissions.ModeOf(PermissionCapability.Calendar));
        Assert.True(settings.Permissions.Ask.Messaging);
    }

    [Fact]
    public async Task AnAskSectionThatIsNotAnObjectGetsTheDefaultsAndANullOneIsRepaired()
    {
        foreach (var section in new[] { "null", "true", "[]" })
        {
            var folder = new SettingsFolder();
            try
            {
                folder.WriteFile($$"""{ "schemaVersion": 1, "permissions": { "calendar": true, "ask": {{section}} } }""");
                var service = folder.CreateService();

                var settings = await service.LoadAsync();

                Assert.Equal(new PermissionAskSettings(), settings.Permissions.Ask);
                Assert.True(settings.Permissions.Calendar);
            }
            finally
            {
                folder.Dispose();
            }
        }
    }

    [Fact]
    public async Task AnAskNameForACapabilityThatCannotAskIsIgnoredAndTheSwitchStillDecides()
    {
        // Files cannot be asked about, and the Ask section has no member for it: a hand-edited name is ignored, and the switch decides.
        _folder.WriteFile("""{ "schemaVersion": 1, "permissions": { "files": true, "ask": { "files": true, "clipboardHistory": true } } }""");
        var settings = await _folder.CreateService().LoadAsync();

        Assert.Equal(PermissionMode.Allowed, settings.Permissions.ModeOf(PermissionCapability.Files));
        Assert.True((await new SettingsPermissionPolicy(_folder.CreateService()).CheckAsync(PermissionCapability.Files)).IsAllowed);
    }
}
