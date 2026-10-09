using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Assistant.Core.Events;
using Assistant.Core.Home;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void ConnectionsAreOptionalAndOAuthCanBeCanceledWithoutFinishingSetup() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        SettingsWait(settings.SaveAsync(new AppSettings { Privacy = new() { LocalOnly = false } }));
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new WaitingSignInConnector();
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector, home: new FakeHomeAssistant());
        using var kit = new SetupKit(settings, connections);
        Assert.Equal(["Microsoft To Do", "Discord", "Beeper", "Home Assistant"], connections.Apps.Select(app => app.Name));
        Assert.Equal(["todo", "discord", "beeper", "homeassistant"], connections.Apps.Select(app => app.Icon));
        Assert.True(kit.Setup.CanContinueConnections);
        connections.ConnectCommand.Execute(connections.Apps[0]);
        SettingsUntil(() => connections.HasSignInLink, "sign-in link became available");
        Assert.True(kit.Setup.IsBusy);
        Assert.False(kit.Setup.CanContinueConnections);
        Assert.False(connections.AddCommand.CanExecute(null));
        Assert.False(settings.LoadAsync().Result.Ui.FirstRunCompleted);
        kit.Setup.CancelCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "sign-in stopped");
        Assert.False(connections.HasSignInLink);
        Assert.True(kit.Setup.CanContinueConnections);
        Assert.False(settings.LoadAsync().Result.Ui.FirstRunCompleted);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloudConnectionRequiresTheUsersChoiceBeforeStartingOAuth(bool allow) => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector);
        var asked = 0;
        connections.ConfirmCloudConnection = name => { Assert.Equal("Microsoft To Do", name); asked++; return allow; };
        connections.ConnectCommand.Execute(connections.Apps[0]);
        SettingsUntil(() => !connections.IsBusy, "cloud choice was applied");
        Assert.Equal(1, asked);
        Assert.Equal(!allow, settings.LoadAsync().Result.Privacy.LocalOnly);
        Assert.Equal(allow ? 1 : 0, connector.Connects.Count);
        if (allow) Assert.Equal("https://mcp.pipedream.net/v2", connector.Connects[0].Endpoint);
        else Assert.Empty(registry.ListAsync().Result);
    });

    [Fact]
    public void Discord_IsConnectedThroughPipedream_AfterTheUserAllowsTheCloud_AsAConnectionOfItsOwn() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector);
        var asked = new List<string>();
        connections.ConfirmCloudConnection = name => { asked.Add(name); return true; };

        var discord = connections.Apps.Single(app => app.Name == "Discord");
        connections.ConnectCommand.Execute(discord);
        SettingsUntil(() => !connections.IsBusy, "Discord was connected");

        Assert.Equal(["Discord"], asked);
        var connect = Assert.Single(connector.Connects);
        Assert.Equal("https://mcp.pipedream.net/v2", connect.Endpoint);
        Assert.Equal("discordpipedream", KnownEndpoints.For("discordpipedream")!.IntegrationId);
        Assert.True(KnownEndpoints.For("discordpipedream")!.IsThroughPipedream);
        Assert.Equal("Discord", KnownApps.ByAppKey("discordpipedream")?.Name);
    });

    [Theory]
    [InlineData("http://remote.example/mcp")]
    [InlineData("https://user:password@remote.example/mcp")]
    [InlineData("https://remote.example/mcp#token")]
    [InlineData("https://remote.example/mcp with spaces")]
    public void AddMcpRejectsInvalidAddressesWithoutCreatingARecord(string address) => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector);
        connections.ServerName = "Example"; connections.ServerAddress = address;
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "address rejected");
        Assert.Empty(registry.ListAsync().Result);
        Assert.Empty(connector.Connects);
        Assert.Contains("HTTPS", connections.Status);
    });

    [Fact]
    public void AccessTokenIsRequiredBeforeCreatingARecordAndNeverStoredInItsJson() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector);
        connections.ServerName = "Local voice"; connections.ServerAddress = "http://localhost:8910/mcp";
        connections.Authentication = "Access token"; connections.ShowAddEditor = true;
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "missing token rejected");
        Assert.Empty(registry.ListAsync().Result);
        connections.PendingToken = "test-secret-never-in-json";
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "token supplied");
        var record = Assert.Single(registry.ListAsync().Result);
        Assert.Empty(IntegrationRules.Problems(record));
        Assert.Equal(IntegrationSourceKind.UserAdded, record.Source.Kind);
        Assert.Equal(IntegrationAuthKind.BearerToken, record.Authentication.Kind);
        Assert.Equal("test-secret-never-in-json", connector.Token);
        Assert.DoesNotContain("test-secret-never-in-json", JsonSerializer.Serialize(record));
        Assert.Null(connections.PendingToken);
        Assert.False(connections.ShowAddEditor);
        Assert.Empty(connector.Connects);
        Assert.True(settings.LoadAsync().Result.Privacy.LocalOnly);
    });

    [Fact]
    public void NoAuthServerReconnectsWithoutOpeningOAuthAndKeepsItsStableId() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        var manager = new FakeConnectedAppManager();
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector, manager, new FakeHomeAssistant());
        connections.ServerName = "Local server"; connections.ServerAddress = "http://localhost:8910/mcp";
        connections.Authentication = "No authentication";
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy && connections.Apps.Count == 5, "server added");
        var record = Assert.Single(registry.ListAsync().Result);
        Assert.Empty(IntegrationRules.Problems(record));
        Assert.False(record.Permissions.LeavesThisPc);
        Assert.Equal(IntegrationAuthKind.None, record.Authentication.Kind);
        SettingsWait(registry.UpdateAsync(record.Id, current => current with { Enabled = false }));
        connections.ConnectCommand.Execute(connections.Apps.Single(app => app.IsCustom));
        SettingsUntil(() => !connections.IsBusy, "server reconnected");
        Assert.True(registry.GetAsync(record.Id).Result!.Enabled);
        Assert.Equal(2, manager.Calls.Count(call => call == "reconnect:" + record.Id));
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "same URL reused");
        Assert.Single(registry.ListAsync().Result);
        Assert.Empty(connector.Connects);
    });

    [Fact]
    public void ConnectionsPageAndCustomServerEditorRenderWithIconsAndNoBindingErrors() => RunSta(() => WithTheme(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var home = new FakeHomeAssistant();
        using var connections = new ConnectionsSetupViewModel(
            settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, new SetupRecordingConnector(registry), home: home);
        using var kit = new SetupKit(settings, connections);
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false);
        using var errors = BindingErrors.Listen();
        try
        {
            window.ShowStep(OnboardingWindow.ConnectionsStepIndex); window.Show(); Pump(); window.UpdateLayout();
            var page = Assert.Single(Descendants<ConnectionsSetupControl>(window));
            var icons = Descendants<Image>(page).ToArray();
            Assert.Equal(4, icons.Length);
            Assert.All(icons, icon => Assert.NotNull(icon.Source));

            // Discord's mark is drawn, in its own blue.
            var discord = Assert.IsType<System.Windows.Media.DrawingImage>(icons[1].Source);
            Assert.True(discord.Height > 0 && discord.Width > 0);
            Assert.Equal(4, Descendants<Button>(page).Count(button => Equals(button.Content, "Connect") && button.IsVisible && button.IsEnabled));
            RenderFixture((FrameworkElement)window.Content, "onboarding-connections-enabled.png");
            connections.AddCommand.Execute(null);
            connections.ServerName = "My MCP"; connections.ServerAddress = "http://localhost:8910/mcp";
            connections.Authentication = "Access token";
            Pump(); window.UpdateLayout();
            Assert.True(Descendants<PasswordBox>(page).Single().IsVisible);
            Assert.DoesNotContain(Descendants<TextBox>(page), field => field.Background is System.Windows.Media.SolidColorBrush brush && brush.Color == System.Windows.Media.Colors.White);
            RenderFixture((FrameworkElement)window.Content, "onboarding-add-mcp.png");

            // Home Assistant's form is its address and a token: no name to give, and no way of signing in to choose.
            connections.CancelEditorCommand.Execute(null);
            connections.ConnectCommand.Execute(connections.Apps.Single(app => app.Name == "Home Assistant"));
            Pump(); window.UpdateLayout();
            Assert.Equal(["Address"], Descendants<TextBox>(page).Where(field => field.IsVisible).Select(field => AutomationProperties.GetName(field)));
            Assert.DoesNotContain(Descendants<ComboBox>(page), box => box.IsVisible);
            Assert.True(Descendants<PasswordBox>(page).Single().IsVisible);
            RenderFixture((FrameworkElement)window.Content, "onboarding-home-assistant.png");
            Assert.Empty(errors.Messages);
        }
        finally { window.Close(); }
        var settingsKit = CreateSettingsKit(service: settings, setup: kit.Setup);
        var (settingsWindow, _, _) = CreateSettingsWindow(settingsKit);
        try
        {
            settingsKit.Model.SelectedSection = settingsKit.Model.Sections.Single(item => item.Section == SettingsSection.Integrations);
            settingsWindow.Show(); Pump(); settingsWindow.UpdateLayout();
            Assert.Single(Descendants<ConnectionsSetupControl>(settingsWindow), page => page.IsVisible);
            Assert.Empty(errors.Messages);
        }
        finally { settingsWindow.CloseForGood(); settingsKit.Model.Dispose(); }
    }));

    [Fact]
    public void HomeAssistantAsksOnlyForItsAddressAndAToken_AndIsConnectedThroughItsOwnApi() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        var home = new FakeHomeAssistant();
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector, home: home);
        var app = connections.Apps.Single(choice => choice.Name == "Home Assistant");
        Assert.Equal(("Not connected", "Connect"), (app.State, app.Label));

        // Its Connect opens the form, which starts from where Home Assistant usually is and asks for a token.
        connections.ConnectCommand.Execute(app);
        Assert.True(connections.ShowAddEditor);
        Assert.True(connections.IsHomeEditor);
        Assert.False(connections.IsServerEditor);
        Assert.True(connections.UsesToken);
        Assert.Equal("Connect Home Assistant", connections.EditorTitle);
        Assert.Equal("http://homeassistant.local:8123", connections.ServerAddress);
        Assert.Contains("Long-lived access tokens", connections.EditorHelp, StringComparison.Ordinal);
        Assert.Contains("Nothing has to be added", connections.EditorHelp, StringComparison.Ordinal);

        // Without a token nothing is tried.
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "the missing token was said");
        Assert.Contains("Paste the access token", connections.Status, StringComparison.Ordinal);
        Assert.Empty(home.Attempts);

        // A token Home Assistant does not take is said, and the form stays to try again.
        home.Answer = HomeFailure.Unauthorized;
        connections.ServerAddress = "192.168.1.20:8123";
        connections.PendingToken = "wrong-token";
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy && home.Attempts.Count == 1, "the token was tried");
        Assert.Contains("did not accept that token", connections.Status, StringComparison.Ordinal);
        Assert.True(connections.ShowAddEditor);
        Assert.Null(connections.PendingToken);

        // The right one connects it: it is on the user's own network, so nobody is asked about Local Only, and it is no MCP connection.
        home.Answer = HomeFailure.None;
        connections.ConfirmCloudConnection = _ => throw new InvalidOperationException("A Home Assistant on the user's own network is not asked about.");
        connections.PendingToken = "token-from-home-assistant";
        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy && home.IsConnected, "Home Assistant was connected");
        Assert.Equal(("192.168.1.20:8123", "token-from-home-assistant"), home.Attempts[^1]);
        Assert.Contains("Home Assistant is connected: 12 devices found", connections.Status, StringComparison.Ordinal);
        Assert.False(connections.ShowAddEditor);
        SettingsUntil(() => app.State == "Connected", "its line says it is connected");
        Assert.Equal("Change", app.Label);
        Assert.Empty(registry.ListAsync().Result);
        Assert.Empty(connector.Connects);
        Assert.Null(connector.Token);
        Assert.True(settings.LoadAsync().Result.Privacy.LocalOnly);

        // Its button now changes the address or the token, starting from the address it has.
        connections.ConnectCommand.Execute(app);
        Assert.True(connections.IsHomeEditor);
        Assert.Equal("http://192.168.1.20:8123", connections.ServerAddress);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHomeAssistantReachedOverTheInternetNeedsTheUsersChoiceWhileLocalOnlyIsOn(bool allow) => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var home = new FakeHomeAssistant();
        using var connections = new ConnectionsSetupViewModel(
            settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, new SetupRecordingConnector(registry), home: home);
        var asked = 0;
        connections.ConfirmCloudConnection = name => { Assert.Equal("Home Assistant", name); asked++; return allow; };
        connections.ConnectCommand.Execute(connections.Apps.Single(choice => choice.Name == "Home Assistant"));
        connections.ServerAddress = "https://home.example.com";
        connections.PendingToken = "token-from-home-assistant";

        connections.SaveServerCommand.Execute(null);
        SettingsUntil(() => !connections.IsBusy, "the choice was applied");

        Assert.Equal(1, asked);
        Assert.Equal(allow, home.IsConnected);
        Assert.Equal(!allow, settings.LoadAsync().Result.Privacy.LocalOnly);
    });

    [Fact]
    public void AConnectedHomeAssistantHasItsOwnLineInSettings_AndCanBeCheckedChangedAndDisconnected() => RunSta(() => WithTheme(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        var home = new FakeHomeAssistant().ConnectedAt("http://192.168.1.20:8123");
        using var connections = new ConnectionsSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), registry, connector, home: home);
        using var kit = new SetupKit(settings, connections);
        var settingsKit = CreateSettingsKit(service: settings, setup: kit.Setup, integrations: new FakeConnectedAppManager(), connector: connector);
        var page = settingsKit.Model.Integrations;
        var (window, _, _) = CreateSettingsWindow(settingsKit);
        using var errors = BindingErrors.Listen();
        try
        {
            SettingsWait(page.RefreshAsync());
            var item = Assert.IsType<HomeAssistantItem>(page.HomeAssistant);

            // It is among the user's connections, with where it is and how it is, and is not offered again among the apps to add.
            Assert.True(page.HasHomeAssistant);
            Assert.False(page.HasNoConnectedApps);
            Assert.Contains("http://192.168.1.20:8123", item.Summary, StringComparison.Ordinal);
            Assert.Equal("Connected · 12 devices", item.Health);
            Assert.False(item.HasProblem);
            Assert.DoesNotContain(page.AvailableApps, app => app.AppKey == "homeassistant");

            settingsKit.Model.SelectedSection = settingsKit.Model.Sections.Single(section => section.Section == SettingsSection.Integrations);
            window.Height = 1500;
            window.Show(); Pump(); window.UpdateLayout();
            item.ToggleDetailsCommand.Execute(null);
            Pump(); window.UpdateLayout();
            Assert.Equal("Hide details", item.DetailsLabel);
            Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "Disconnect") && button.IsVisible);
            RenderFixture((FrameworkElement)window.Content, "settings-home-assistant.png", 1);

            // One that stops answering is said to, as a problem.
            home.Answer = HomeFailure.Unreachable;
            item.CheckCommand.Execute(null);
            SettingsUntil(() => item.HasProblem, "the problem was shown");
            Assert.Equal("Did not answer just now", item.Health);

            // Changing it opens its form under Add a connection, with the address it has.
            item.ChangeCommand.Execute(null);
            Assert.True(connections.ShowAddEditor);
            Assert.True(connections.IsHomeEditor);
            Assert.Equal("http://192.168.1.20:8123", connections.ServerAddress);
            connections.CancelEditorCommand.Execute(null);

            // Disconnecting takes its line away and offers it again.
            item.DisconnectCommand.Execute(null);
            SettingsUntil(() => !page.HasHomeAssistant && page.AvailableApps.Any(app => app.AppKey == "homeassistant"), "Home Assistant was disconnected and offered again");
            Assert.False(home.IsConnected);
            Assert.True(page.HasNoConnectedApps);
            Assert.Empty(errors.Messages);
        }
        finally { window.CloseForGood(); settingsKit.Model.Dispose(); }
    }));

    private sealed class SetupConnectionStore : IInstalledIntegrationStore
    {
        private IReadOnlyList<InstalledIntegration> _saved = [];
        public Task<IntegrationStoreContents> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationStoreContents(_saved));
        public Task SaveAsync(IReadOnlyList<InstalledIntegration> integrations, CancellationToken cancellationToken = default) { _saved = [.. integrations]; return Task.CompletedTask; }
    }

    private sealed class SetupRecordingConnector(IInstalledIntegrationRegistry registry) : IIntegrationConnector
    {
        public List<KnownEndpoint> Connects { get; } = [];
        public string? Token { get; private set; }
        public async Task<InstallOutcome> ConnectAsync(KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null)
        {
            Connects.Add(endpoint);
            if (await registry.GetAsync(endpoint.IntegrationId, cancellationToken) is null)
                await registry.AddAsync(new InstalledIntegration
                {
                    Id = endpoint.IntegrationId, Name = endpoint.Name, Enabled = true,
                    Transport = new() { Kind = McpTransportKind.StreamableHttp, Endpoint = endpoint.Endpoint },
                    Authentication = new() { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.Ready },
                    Health = new() { Status = IntegrationHealthStatus.Healthy },
                }, cancellationToken);
            return new() { Status = InstallStatus.Installed, Message = "Connected." };
        }
        public Task<InstallOutcome> SignInAsync(string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null) => throw new NotSupportedException();
        public Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default)
        {
            Token = token;
            await registry.UpdateAsync(integrationId, record => record with
            {
                Authentication = record.Authentication with { State = IntegrationAuthState.Ready },
                Health = new() { Status = IntegrationHealthStatus.Healthy },
            }, cancellationToken);
            return new() { Status = InstallStatus.Installed, Message = "Connected." };
        }
        public Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default) => UseTokenAsync(integrationId, value, cancellationToken);
    }
}
