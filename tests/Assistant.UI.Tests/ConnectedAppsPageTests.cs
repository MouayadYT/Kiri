using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.UI.Messages;
using Assistant.UI.Settings;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>A manager of connected apps the test controls: what it lists, how each action goes, and what was asked of it.</summary>
internal sealed class FakeConnectedAppManager : IIntegrationManager
{
    public event EventHandler? Changed;

    public List<IntegrationInfo> Items { get; } = [];

    public List<string> Calls { get; } = [];

    public UpdateCheckState CheckState { get; set; } = UpdateCheckState.Ready;

    public UpdateCheckSummary Summary { get; set; } = new(UpdateCheckState.Ready, 1, 1, 0, "1 update is available.");

    public ReconnectOutcome Reconnect { get; set; } = new(true, "Todoist is connected. It offers 2 tools.", 2);

    public UpdatePreparation Preparation { get; set; } = new(UpdatePreparationStatus.UpToDate, "Todoist is up to date.");

    public RemoveOutcome Removal { get; set; } = new(true, true, "Todoist was removed.");

    public Exception? EnableFails { get; set; }

    public TaskCompletionSource? ReconnectGate { get; set; }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IntegrationInfo>>([.. Items]);

    public Task<UpdateCheckState> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(CheckState);

    public Task SetEnabledAsync(string integrationId, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add($"enable:{integrationId}:{enabled}");
        return EnableFails is null ? Task.CompletedTask : Task.FromException(EnableFails);
    }

    public List<IntegrationAccessChange> AccessChanges { get; } = [];

    public Exception? AccessFails { get; set; }

    public Task<IntegrationInfo?> SetAccessAsync(string integrationId, IntegrationAccessChange change, CancellationToken cancellationToken = default)
    {
        Calls.Add("access:" + integrationId);
        AccessChanges.Add(change);
        if (AccessFails is not null)
        {
            return Task.FromException<IntegrationInfo?>(AccessFails);
        }

        var index = Items.FindIndex(item => item.Id == integrationId);
        if (index < 0)
        {
            return Task.FromResult<IntegrationInfo?>(null);
        }

        var access = Items[index].Access;
        Items[index] = Items[index] with
        {
            Access = access with
            {
                Reads = change.Reads ?? access.Reads, Changes = change.Changes ?? access.Changes, Network = change.Network ?? access.Network,
                Account = change.Account ?? access.Account, Updates = change.Updates ?? access.Updates,
            },
        };
        return Task.FromResult<IntegrationInfo?>(Items[index]);
    }

    public async Task<ReconnectOutcome> ReconnectAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("reconnect:" + integrationId);
        if (ReconnectGate is not null)
        {
            await ReconnectGate.Task;
        }

        return Reconnect;
    }

    public Task<UpdateCheckSummary> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default)
    {
        Calls.Add("check:" + force);
        return Task.FromResult(Summary);
    }

    public Task<UpdatePreparation> PrepareUpdateAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("prepare:" + integrationId);
        return Task.FromResult(Preparation);
    }

    public Task<RemoveOutcome> RemoveAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("remove:" + integrationId);
        Items.RemoveAll(item => item.Id == integrationId);
        return Task.FromResult(Removal);
    }
}

/// <summary>A connector whose sign-in waits for the user the way the real one does, until it is finished or stopped, and records how the page was asked to be shown.</summary>
internal sealed class WaitingSignInConnector : IIntegrationConnector
{
    public Assistant.Tools.Mcp.Auth.OAuthSignInOptions? Options { get; private set; }

    public int SignIns { get; private set; }

    public TaskCompletionSource<InstallOutcome> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri Page { get; } = new("https://login.example.com/authorize?client_id=x&prompt=select_account");

    public async Task<InstallOutcome> SignInAsync(
        string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default,
        Assistant.Tools.Mcp.Auth.OAuthSignInOptions? options = null)
    {
        SignIns++;
        Options = options;
        options?.AddressReady?.Invoke(Page);
        using var stopped = cancellationToken.Register(() => Finish.TrySetResult(
            InstallOutcome.Fail(InstallFailure.SignInFailed, "I stopped before you signed in to Todoist, so nothing was connected.")));
        return await Finish.Task;
    }

    public Task<InstallOutcome> ConnectAsync(
        KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default,
        Assistant.Tools.Mcp.Auth.OAuthSignInOptions? options = null) => SignInAsync(endpoint.IntegrationId, progress, cancellationToken, options);

    public Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed partial class PromptInputControlTests
{
    private static IntegrationInfo Info(
        string id = "todoist", string name = "Todoist", string version = "13.3.0", bool enabled = true, string health = "Connected",
        IntegrationHealthLevel level = IntegrationHealthLevel.Good, string? update = null, bool canUpdate = true, bool managed = true, int tools = 2) => new()
    {
        Id = id,
        Name = name,
        Source = "Official MCP registry: @doist/todoist-mcp",
        Version = version,
        Enabled = enabled,
        Health = health,
        HealthLevel = level,
        Permissions = "Runs on this PC, asks before it changes anything.",
        UpdateVersion = update,
        CanUpdate = canUpdate,
        IsManaged = managed,
        ToolCount = tools,
        IsLocalProgram = true,
    };

    private static (SettingsKit Kit, FakeConnectedAppManager Manager, RecordingBroker Broker) ConnectedKit(
        AppSettings? saved = null, params IntegrationInfo[] items)
    {
        var manager = new FakeConnectedAppManager();
        manager.Items.AddRange(items);
        var broker = new RecordingBroker();
        var kit = CreateSettingsKit(saved, integrations: manager, offers: broker);
        return (kit, manager, broker);
    }

    private static ConnectedAppItem AppItem(SettingsKit kit, string id = "todoist") => kit.Model.Integrations.ConnectedApps.Single(item => item.Id == id);

    // ---- what the page lists ------------------------------------------------------------------------------------------

    [Fact]
    public void ThePageListsTheConnectedAppsWithWhatTheAssistantKnowsOfEach() => RunSta(() =>
    {
        var (kit, _, _) = ConnectedKit(null, Info(update: "13.4.0"), Info("notes", "Notes", enabled: false, health: "Turned off", level: IntegrationHealthLevel.Unknown, canUpdate: false, managed: false, tools: 0));

        var page = kit.Model.Integrations;

        Assert.True(page.HasManager);
        Assert.True(page.HasConnectedApps);
        Assert.False(page.HasNoConnectedApps);
        Assert.Equal(["todoist", "notes"], page.ConnectedApps.Select(item => item.Id).ToArray());
        var todoist = page.ConnectedApps[0];
        Assert.Equal("Todoist", todoist.Name);
        Assert.Equal("Official MCP registry: @doist/todoist-mcp · Version 13.3.0", todoist.Summary);
        Assert.Equal("Connected", todoist.Health);
        Assert.Equal(IntegrationHealthLevel.Good, todoist.HealthLevel);
        Assert.Equal("Runs on this PC, asks before it changes anything.", todoist.Permissions);
        Assert.Equal("Offers 2 tools.", todoist.ToolsText);
        Assert.True(todoist.HasUpdate);
        Assert.Equal("Update available: version 13.4.0", todoist.UpdateText);
        Assert.Equal("Update", todoist.UpdateLabel);
        Assert.True(todoist.Enabled && todoist.CanUpdate);
        var notes = page.ConnectedApps[1];
        Assert.False(notes.Enabled);
        Assert.False(notes.HasUpdate);
        Assert.Equal("Check for update", notes.UpdateLabel);
        Assert.Equal("Its tools are read the first time it is used.", notes.ToolsText);
        Assert.False(notes.CanUpdate);
        Assert.Contains("itself is not touched", notes.RemoveQuestion, StringComparison.Ordinal);
        Assert.Contains("files the Assistant installed", todoist.RemoveQuestion, StringComparison.Ordinal);
    });

    [Fact]
    public void WithNothingInstalledThePageSaysHowAnIntegrationGetsInstalled() => RunSta(() =>
    {
        var (kit, _, _) = ConnectedKit();

        var page = kit.Model.Integrations;

        Assert.True(page.HasNoConnectedApps);
        Assert.Equal("Nothing connected yet.", page.EmptyText);
    });

    [Fact]
    public void WithoutAManagerTheConnectedAppsAreNotShownAtAll() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        Assert.False(kit.Model.Integrations.HasManager);
        Assert.Empty(kit.Model.Integrations.ConnectedApps);
        Assert.False(kit.Model.Integrations.CheckNowCommand.CanExecute(null));
    });

    [Fact]
    public void WhenTheListChangesTheOpenPageShowsIt() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        manager.Items.Add(Info("notes", "Notes"));
        manager.Items[0] = Info(version: "13.4.0");

        manager.RaiseChanged();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => kit.Model.Integrations.ConnectedApps.Count == 2, "The page did not show the new integration.");

        Assert.Equal("Official MCP registry: @doist/todoist-mcp · Version 13.4.0", AppItem(kit).Summary);
    });

    // ---- turning on and off ------------------------------------------------------------------------------------------

    [Fact]
    public void TheSwitchTurnsTheIntegrationOffAndOnAtOnceAndSaysSo() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        var item = AppItem(kit);

        item.Enabled = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.Message.Length > 0, "No message.");
        Assert.Equal("Todoist is turned off.", item.Message);
        item.Enabled = true;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.Message.Contains("on", StringComparison.Ordinal), "No message.");

        Assert.Equal(["enable:todoist:False", "enable:todoist:True"], manager.Calls);
        Assert.Equal("Todoist is turned on. It starts when you need it.", item.Message);
    });

    [Fact]
    public void ASwitchThatCannotBeChangedGoesBackAndSaysWhy() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        manager.EnableFails = new IntegrationException(IntegrationFailure.StoreFailed);
        var item = AppItem(kit);

        item.Enabled = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.Message.Length > 0, "No message.");

        Assert.True(item.Enabled);
        Assert.Contains("could not be turned off", item.Message, StringComparison.Ordinal);
    });

    // ---- what the user lets the app do (step 119) -----------------------------------------------------------------------

    private static IntegrationInfo Reaching(bool network = true, bool account = true, bool updates = true) =>
        Info() with { Access = new IntegrationAccess { NetworkApplies = network, AccountApplies = account, UpdatesApply = updates } };

    [Fact]
    public void EachChoiceForWhatTheAppMayDoIsMadeAtOnceAndAsksTheManagerToChangeOnlyThatChoice() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Reaching());
        var item = AppItem(kit);
        Assert.True(item.AllowReads && item.AllowChanges && item.AllowNetwork && item.AllowAccount && item.AllowUpdates);

        item.AllowReads = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.AccessChanges.Count == 1, "Reads not changed.");
        item.AllowChanges = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.AccessChanges.Count == 2, "Changes not changed.");
        item.AllowNetwork = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.AccessChanges.Count == 3, "Network not changed.");
        item.AllowAccount = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.AccessChanges.Count == 4, "Account not changed.");
        item.AllowUpdates = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.AccessChanges.Count == 5, "Updates not changed.");

        Assert.Equal(
            [
                new IntegrationAccessChange { Reads = false }, new IntegrationAccessChange { Changes = false }, new IntegrationAccessChange { Network = false },
                new IntegrationAccessChange { Account = false }, new IntegrationAccessChange { Updates = false },
            ],
            manager.AccessChanges);
        Assert.False(item.AllowReads || item.AllowChanges || item.AllowNetwork || item.AllowAccount || item.AllowUpdates);
        Assert.Equal("What Todoist may do was changed.", item.Message);
    });

    [Fact]
    public void ASwitchSetToWhatItAlreadyIsDoesNothing() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Reaching());
        var item = AppItem(kit);

        item.AllowReads = true;
        item.AllowNetwork = true;

        Assert.Empty(manager.AccessChanges);
    });

    [Fact]
    public void OnlyTheChoicesThatMeanSomethingForTheAppAreOffered() => RunSta(() =>
    {
        var (kit, _, _) = ConnectedKit(null, Reaching(network: false, account: false, updates: false), Reaching(network: true, account: true, updates: true) with { Id = "notes", Name = "Notes" });

        var program = AppItem(kit);
        var hosted = AppItem(kit, "notes");

        Assert.False(program.ShowsNetwork || program.ShowsAccount || program.ShowsUpdates);
        Assert.True(hosted.ShowsNetwork && hosted.ShowsAccount && hosted.ShowsUpdates);
    });

    [Fact]
    public void AChoiceThatCouldNotBeSavedGoesBackAndSaysSo() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Reaching());
        manager.AccessFails = new IntegrationException(IntegrationFailure.StoreFailed);
        var item = AppItem(kit);

        item.AllowReads = false;
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.Message.Length > 0, "No message.");

        Assert.True(item.AllowReads);
        Assert.Contains("could not be changed", item.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void ShowingTheListAgainChangesNoChoice() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Reaching() with { Access = new IntegrationAccess { Reads = false, NetworkApplies = true } });
        var item = AppItem(kit);

        manager.RaiseChanged();
        kit.Settle();

        Assert.False(item.AllowReads);
        Assert.Empty(manager.AccessChanges);
    });

    [Fact]
    public void LookingForANewerVersionIsAskedAboutWhenTheWebPermissionAsksAndNothingIsLookedUpOnANo() => RunSta(() =>
    {
        var prompt = new RecordingPrompt(Assistant.Core.Confirmation.ConfirmationDecision.Declined);
        var manager = new FakeConnectedAppManager();
        manager.Items.Add(Info());
        var kit = CreateSettingsKit(null, integrations: manager, permissionGate: AskingGate(prompt, PermissionCapability.ExternalSearch));
        var item = AppItem(kit);

        item.UpdateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.Message.Length > 0, "No message.");

        Assert.Equal(PermissionCapability.ExternalSearch, Assert.Single(prompt.Asked).Capability);
        Assert.DoesNotContain("prepare:todoist", manager.Calls);
        Assert.Contains("did not allow", item.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void LookingForANewerVersionGoesAheadOnAYes() => RunSta(() =>
    {
        var prompt = new RecordingPrompt();
        var manager = new FakeConnectedAppManager();
        manager.Items.Add(Info());
        var kit = CreateSettingsKit(null, integrations: manager, permissionGate: AskingGate(prompt, PermissionCapability.ExternalSearch));
        var item = AppItem(kit);

        item.UpdateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.Calls.Contains("prepare:todoist"), "The manager was not asked.");

        Assert.Single(prompt.Asked);
    });

    [Fact]
    public void ShowingTheListAgainDoesNotTurnAnythingOnOrOff() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        manager.Items[0] = Info(enabled: false);

        var refreshing = kit.Model.Integrations.RefreshAsync();
        WaitForTask(refreshing);

        Assert.False(AppItem(kit).Enabled);
        Assert.Empty(manager.Calls);
    });

    // ---- reconnect, update, remove -----------------------------------------------------------------------------------

    [Fact]
    public void ReconnectingShowsThatItIsWorkingThenHowItWent() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        manager.ReconnectGate = new TaskCompletionSource();
        var item = AppItem(kit);

        item.ReconnectCommand.Execute(null);
        Assert.True(item.Busy);
        Assert.Equal("Connecting to Todoist…", item.Message);
        Assert.False(item.ReconnectCommand.CanExecute(null));

        manager.ReconnectGate.SetResult();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !item.Busy, "It did not finish.");
        Assert.Equal("Todoist is connected. It offers 2 tools.", item.Message);
        Assert.Equal(["reconnect:todoist"], manager.Calls);
    });

    [Fact]
    public void ATurnedOffIntegrationCannotBeReconnected() => RunSta(() =>
    {
        var (kit, _, _) = ConnectedKit(null, Info(enabled: false));

        Assert.False(AppItem(kit).ReconnectCommand.CanExecute(null));
    });

    [Fact]
    public void UpdatingShowsWhatWouldBeInstalledForTheUserToApproveAndChangesNothingYet() => RunSta(() =>
    {
        var (kit, manager, broker) = ConnectedKit(null, Info(update: "13.4.0"));
        manager.Preparation = new UpdatePreparation(UpdatePreparationStatus.Offered, "Version 13.4.0 is available.", OfferFor(IntegrationOfferKind.Update));
        var item = AppItem(kit);

        item.UpdateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.IsOfferOpen, "The update was not shown.");

        Assert.Equal(["prepare:todoist"], manager.Calls);
        var panel = item.Offer!;
        Assert.Equal("Update Todoist to version 13.4.0?", panel.Title);
        Assert.Equal(IntegrationOfferState.Waiting, panel.State);
        Assert.Empty(broker.Accepted);
        Assert.False(item.UpdateCommand.CanExecute(null));

        panel.InstallCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsInstalled, "It did not finish.");
        Assert.Equal(["offer-1"], broker.Accepted);
    });

    [Fact]
    public void TurningDownAnUpdateRemovesThePanelAndSaysNothingChanged() => RunSta(() =>
    {
        var (kit, manager, broker) = ConnectedKit(null, Info(update: "13.4.0"));
        manager.Preparation = new UpdatePreparation(UpdatePreparationStatus.Offered, "Version 13.4.0 is available.", OfferFor(IntegrationOfferKind.Update));
        var item = AppItem(kit);
        item.UpdateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.IsOfferOpen, "The update was not shown.");

        item.Offer!.CancelCommand.Execute(null);

        Assert.False(item.IsOfferOpen);
        Assert.Null(item.Offer);
        Assert.Equal(["offer-1"], broker.Declined);
        Assert.Equal("Not updated. Nothing was downloaded or changed.", item.Message);
        Assert.True(item.UpdateCommand.CanExecute(null));
    });

    [Theory]
    [InlineData(UpdatePreparationStatus.UpToDate, "Todoist is up to date.")]
    [InlineData(UpdatePreparationStatus.Blocked, "Updating needs the web: turn Local Only mode off in Settings > Privacy and allow External Web and Image Search in Settings > Permissions.")]
    [InlineData(UpdatePreparationStatus.NotPassed, "The newest version did not pass my checks: npm publishes no checksum for it.")]
    public void AnUpdateThatIsNotOfferedSaysWhyAndShowsNoPanel(UpdatePreparationStatus status, string message) => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        manager.Preparation = new UpdatePreparation(status, message);
        var item = AppItem(kit);

        item.UpdateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !item.Busy && item.Message == message, "The reason was not shown.");

        Assert.False(item.IsOfferOpen);
    });

    [Fact]
    public void RemovingAsksOnceMoreAndTheAnswerKeepOrRemoveDecides() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info(), Info("notes", "Notes"));
        var item = AppItem(kit);

        item.RemoveCommand.Execute(null);
        Assert.True(item.ConfirmingRemove);
        Assert.Empty(manager.Calls);
        item.KeepCommand.Execute(null);
        Assert.False(item.ConfirmingRemove);
        Assert.Empty(manager.Calls);

        item.RemoveCommand.Execute(null);
        item.ConfirmRemoveCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => kit.Model.Integrations.ConnectedApps.Count == 1, "It was not taken out of the list.");

        Assert.Equal(["remove:todoist"], manager.Calls);
        Assert.Equal(["notes"], kit.Model.Integrations.ConnectedApps.Select(app => app.Id).ToArray());
        Assert.False(kit.Model.Integrations.HasNoConnectedApps);
    });

    [Fact]
    public void RemovingTheLastOneShowsTheEmptyPageAndRemovingNeverAsksTwice() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        var item = AppItem(kit);
        item.RemoveCommand.Execute(null);

        item.ConfirmRemoveCommand.Execute(null);
        item.ConfirmRemoveCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => kit.Model.Integrations.HasNoConnectedApps, "The list did not empty.");

        Assert.Equal(["remove:todoist"], manager.Calls);
    });

    // ---- looking for updates -------------------------------------------------------------------------------------------

    [Fact]
    public void LookingForUpdatesIsOffUntilTheUserTurnsItOnAndThenItIsSavedAndTheLookIsMade() => RunSta(() =>
    {
        var (kit, manager, _) = ConnectedKit(null, Info());
        var page = kit.Model.Integrations;
        Assert.False(page.CheckForUpdates);
        Assert.False(page.CheckNowCommand.CanExecute(null));
        Assert.DoesNotContain("check:False", manager.Calls);

        page.CheckForUpdates = true;
        kit.Settle();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.UpdateSummary.Length > 0, "No summary.");

        Assert.True(kit.Saved.Integrations.CheckForIntegrationUpdates);
        Assert.Contains("check:False", manager.Calls);
        Assert.Equal("1 update is available.", page.UpdateSummary);
        Assert.True(page.CheckNowCommand.CanExecute(null));
    });

    [Fact]
    public void WhenItIsOnOpeningThePageLooksAtMostOnceADayAndShowsWhatItFound() => RunSta(() =>
    {
        var saved = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true } };
        var manager = new FakeConnectedAppManager();
        manager.Items.Add(Info(update: "13.4.0"));
        var kit = CreateSettingsKit(saved, integrations: manager, offers: new RecordingBroker());

        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.Calls.Contains("check:False"), "It did not look.");

        Assert.DoesNotContain("check:True", manager.Calls);
        Assert.True(kit.Model.Integrations.CheckForUpdates);
        Assert.Equal("1 update is available.", kit.Model.Integrations.UpdateSummary);
        Assert.True(AppItem(kit).HasUpdate);
    });

    [Fact]
    public void WhileItIsBlockedTheLookIsNotMadeAndThePageSaysWhatIsNeeded() => RunSta(() =>
    {
        var saved = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true } };
        var manager = new FakeConnectedAppManager { CheckState = UpdateCheckState.Blocked };
        manager.Items.Add(Info());
        var kit = CreateSettingsKit(saved, integrations: manager, offers: new RecordingBroker());

        WaitUntilFor(TimeSpan.FromSeconds(5), () => kit.Model.Integrations.UpdateNote.StartsWith("Looking for updates needs the web", StringComparison.Ordinal), "No explanation.");

        Assert.DoesNotContain(manager.Calls, call => call.StartsWith("check:", StringComparison.Ordinal));
        Assert.Contains("Local Only", kit.Model.Integrations.UpdateNote, StringComparison.Ordinal);
    });

    [Fact]
    public void CheckNowLooksAtOnceWhateverTheLastLookWas() => RunSta(() =>
    {
        var saved = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true } };
        var (kit, manager, _) = ConnectedKit(saved, Info());
        manager.Summary = new UpdateCheckSummary(UpdateCheckState.Ready, 1, 0, 0, "Everything is up to date.");

        kit.Model.Integrations.CheckNowCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => manager.Calls.Contains("check:True"), "It did not look.");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !kit.Model.Integrations.IsChecking, "It did not finish.");

        Assert.Equal("Everything is up to date.", kit.Model.Integrations.UpdateSummary);
    });

    [Fact]
    public void TurningLookingOffClearsWhatWasFoundAndSavesTheChoice() => RunSta(() =>
    {
        var saved = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true } };
        var (kit, _, _) = ConnectedKit(saved, Info());
        WaitUntilFor(TimeSpan.FromSeconds(5), () => kit.Model.Integrations.UpdateSummary.Length > 0, "No summary.");

        kit.Model.Integrations.CheckForUpdates = false;
        kit.Settle();

        Assert.False(kit.Saved.Integrations.CheckForIntegrationUpdates);
        Assert.Equal(string.Empty, kit.Model.Integrations.UpdateSummary);
    });

    // ---- how the page is drawn -----------------------------------------------------------------------------------------

    [Fact]
    public void TheIntegrationsPageIsDrawnWithEachAppsButtonsAndNoBindingErrors() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, manager, _) = ConnectedKit(null, Info(update: "13.4.0"), Info("notes", "Notes", enabled: false, health: "Turned off", level: IntegrationHealthLevel.Unknown, canUpdate: false, managed: false));
        manager.Preparation = new UpdatePreparation(UpdatePreparationStatus.Offered, "Version 13.4.0 is available.", OfferFor(IntegrationOfferKind.Update));
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Integrations);
            Pump();
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.Contains("Your connections", texts);
            Assert.Contains("Todoist", texts);
            Assert.Contains("Notes", texts);
            Assert.Contains("Connected", texts);
            Assert.Contains("Turned off", texts);
            Assert.Contains("Update available: version 13.4.0", texts);
            Assert.Contains("Updates", texts);

            // Each app is a line with its switch; what it may do and the buttons that reconnect, update and remove it are behind Manage.
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Manage" && button.IsVisible));
            Assert.Equal(0, Descendants<Button>(window).Count(button => button.Content as string == "Reconnect" && button.IsVisible));
            foreach (var connected in kit.Model.Integrations.ConnectedApps)
            {
                connected.ToggleDetailsCommand.Execute(null);
            }

            Pump();
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Hide details" && button.IsVisible));
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Reconnect" && button.IsVisible));
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Remove" && button.IsVisible));

            // Update shows only for what the Assistant installed from a registry.
            Assert.Equal(1, Descendants<Button>(window).Count(button => button.Content as string is "Update" or "Check for update" && button.IsVisible));
            Assert.Contains("File Explorer", texts);

            var first = AppItem(kit);
            first.UpdateCommand.Execute(null);
            WaitUntilFor(TimeSpan.FromSeconds(5), () => first.IsOfferOpen, "No update panel.");
            Pump();
            Assert.Contains(AllTextOf(window), text => text == "Update Todoist to version 13.4.0?");
            RenderFixture(window, "settings-integrations-connected-apps.png", 2);

            first.Offer!.CancelCommand.Execute(null);
            first.RemoveCommand.Execute(null);
            Pump();
            Assert.Contains(AllTextOf(window), text => text.StartsWith("Remove Todoist? The files the Assistant installed for it are deleted.", StringComparison.Ordinal));
            Assert.True(ButtonsVisible(window, "Keep it"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void WithNothingInstalledTheIntegrationsPageShowsTheEmptyWordsAndTheOtherSettingsStillWork() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, _, _) = ConnectedKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Integrations);
            Pump();

            Assert.Contains(AllTextOf(window), text => text == "Nothing connected yet.");
            Assert.Contains(AllTextOf(window), text => text == "File Explorer");
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void WithoutAManagerThePageShowsOnlyTheOtherApps() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Integrations);
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.DoesNotContain("Connected apps", texts);
            Assert.DoesNotContain("Updates", texts);
            Assert.Contains("File Explorer", texts);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void TheSettingsWindowOpensOnTheIntegrationsPageWhenAskedTo() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        var kit = CreateSettingsKit();
        var (window, _, placement) = CreateSettingsWindow(kit);
        try
        {
            window.ShowAndActivate(SettingsSection.Integrations);
            Pump();

            Assert.Equal(SettingsSection.Integrations, kit.Model.SelectedSection.Section);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    // ---- signing in as the account wanted ------------------------------------------------------------------------------

    private static (SettingsKit Kit, FakeConnectedAppManager Manager, WaitingSignInConnector Connector) SigningKit()
    {
        var manager = new FakeConnectedAppManager();
        manager.Items.Add(Info() with { CanSignIn = true, NeedsSignIn = true });
        var connector = new WaitingSignInConnector();
        return (CreateSettingsKit(integrations: manager, connector: connector), manager, connector);
    }

    [Fact]
    public void ASignInWaitsWithItsLinkToCopyAndAWayToStop_AndAPrivateWindowIsAskedForWhenChosen() => RunSta(() =>
    {
        var (kit, _, connector) = SigningKit();
        var item = AppItem(kit);
        string? copied = null;
        item.CopyText = text => { copied = text; return true; };
        Assert.True(item.SignInCommand.CanExecute(null));
        Assert.True(item.SignInPrivateCommand.CanExecute(null));
        Assert.False(item.IsSigningIn);
        Assert.False(item.CopySignInLinkCommand.CanExecute(null));
        Assert.False(item.CancelSignInCommand.CanExecute(null));

        item.SignInPrivateCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.HasSignInLink, "The link of the waiting sign-in was not shown.");

        // It waits: the page asked for a private window, the link is the service's own page, and nothing else can be started meanwhile.
        Assert.True(connector.Options!.PrivateWindow);
        Assert.True(item.IsSigningIn);
        Assert.True(item.Busy);
        Assert.Equal(connector.Page.AbsoluteUri, item.SignInLink);
        Assert.False(item.SignInCommand.CanExecute(null));
        Assert.False(item.SignInPrivateCommand.CanExecute(null));
        Assert.Contains("private browser window", item.Message, StringComparison.Ordinal);

        // The link can be copied, to open in whichever browser or profile has the account wanted.
        Assert.True(item.CopySignInLinkCommand.CanExecute(null));
        item.CopySignInLinkCommand.Execute(null);
        Assert.Equal(connector.Page.AbsoluteUri, copied);
        Assert.Contains("copied", item.Message, StringComparison.Ordinal);

        // And the wait can be ended at any time: a page that failed in the browser (a school account the organization does not allow) never comes back here.
        item.CancelSignInCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !item.IsSigningIn, "The sign-in was not stopped.");

        Assert.False(item.Busy);
        Assert.False(item.HasSignInLink);
        Assert.Equal(string.Empty, item.SignInLink);
        Assert.Contains("stopped before you signed in", item.Message, StringComparison.Ordinal);
        Assert.True(item.SignInCommand.CanExecute(null));
        Assert.True(item.SignInPrivateCommand.CanExecute(null));
        Assert.False(item.CancelSignInCommand.CanExecute(null));
    });

    [Fact]
    public void TheOrdinarySignInUsesTheDefaultBrowserAndSaysHowToChooseAnotherAccount_AndAFinishedSignInClearsTheLink() => RunSta(() =>
    {
        var (kit, _, connector) = SigningKit();
        var item = AppItem(kit);

        item.SignInCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.HasSignInLink, "The link of the waiting sign-in was not shown.");

        Assert.False(connector.Options!.PrivateWindow);
        Assert.Contains("Use another account", item.Message, StringComparison.Ordinal);
        Assert.Contains("copy the sign-in link", item.Message, StringComparison.Ordinal);

        connector.Finish.SetResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Todoist is connected." });
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !item.IsSigningIn, "The sign-in did not end.");

        Assert.Equal("You are signed in to Todoist.", item.Message);
        Assert.False(item.HasSignInLink);
        Assert.False(item.Busy);
        Assert.Equal(1, connector.SignIns);
    });

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of a connected app that needs a sign-in, before and while one waits.
    [Fact]
    public void RenderTheSignInButtonsWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var (kit, _, connector) = SigningKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Height = 1700;
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Integrations);
            window.UpdateLayout();
            Pump();
            RenderFixture(Named<Grid>(window, "Root"), "settings-signin-idle.png", 1);

            AppItem(kit).SignInPrivateCommand.Execute(null);
            WaitUntilFor(TimeSpan.FromSeconds(5), () => AppItem(kit).HasSignInLink, "The link of the waiting sign-in was not shown.");
            window.UpdateLayout();
            Pump();
            RenderFixture(Named<Grid>(window, "Root"), "settings-signin-waiting.png", 1);

            connector.Finish.SetResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Todoist is connected." });
            WaitUntilFor(TimeSpan.FromSeconds(5), () => !AppItem(kit).IsSigningIn, "The sign-in did not end.");
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void ConnectingAnAppFromTheListWaitsTheSameWay_WithItsLinkToCopyAndAWayToStop() => RunSta(() =>
    {
        var (kit, _, connector) = SigningKit();
        var page = kit.Model.Integrations;
        string? copied = null;
        page.CopyText = text => { copied = text; return true; };
        var app = page.AvailableApps.First();
        Assert.False(page.IsConnecting);
        Assert.False(page.CancelConnectCommand.CanExecute(null));

        app.ConnectCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.HasConnectLink, "The link of the waiting sign-in was not shown.");

        Assert.True(page.IsConnecting);
        Assert.Equal(connector.Page.AbsoluteUri, page.ConnectLink);
        Assert.Contains("Use another account", page.ConnectStatus, StringComparison.Ordinal);
        page.CopyConnectLinkCommand.Execute(null);
        Assert.Equal(connector.Page.AbsoluteUri, copied);

        page.CancelConnectCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !page.IsConnecting, "The sign-in was not stopped.");

        Assert.False(page.HasConnectLink);
        Assert.Contains("stopped before you signed in", page.ConnectStatus, StringComparison.Ordinal);
        Assert.False(page.CancelConnectCommand.CanExecute(null));
    });

    [Fact]
    public void ALinkThatCannotBeCopiedSaysSo_AndTheSignInKeepsWaiting() => RunSta(() =>
    {
        var (kit, _, connector) = SigningKit();
        var item = AppItem(kit);
        item.CopyText = _ => false;

        item.SignInCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => item.HasSignInLink, "The link of the waiting sign-in was not shown.");
        item.CopySignInLinkCommand.Execute(null);

        Assert.Equal("The clipboard is busy. Try again.", item.Message);
        Assert.True(item.IsSigningIn);

        connector.Finish.SetResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Todoist is connected." });
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !item.IsSigningIn, "The sign-in did not end.");
    });
}

