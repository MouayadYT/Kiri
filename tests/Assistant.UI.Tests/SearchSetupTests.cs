using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Search;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void SavingAKeyClearsBothEditorsWhenSettingsAndOnboardingShareTheSetup() => RunSta(() => WithTheme(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), new SetupSearchService(), registry, connector);
        search.Enabled = true;
        var first = new SearchSetupControl { DataContext = search }; var second = new SearchSetupControl { DataContext = search };
        var panel = new StackPanel(); panel.Children.Add(first); panel.Children.Add(second);
        var window = new Window { Content = panel, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            var firstKey = Named<PasswordBox>(first, "ApiKey"); var secondKey = Named<PasswordBox>(second, "ApiKey");
            firstKey.Password = "previous-private-key"; secondKey.Password = "current-private-key";
            search.UseKeyCommand.Execute(null);
            SettingsUntil(() => !search.IsBusy, "key saved and editors cleared");
            Assert.Equal("current-private-key", connector.Token);
            Assert.Null(search.PendingApiKey);
            Assert.Empty(firstKey.Password); Assert.Empty(secondKey.Password);
        }
        finally { window.Close(); }
    }));
    [Theory]
    [InlineData(WebSearchProvider.Exa, IntegrationAuthKind.HeaderKey, "x-api-key")]
    [InlineData(WebSearchProvider.Tavily, IntegrationAuthKind.BearerToken, "Authorization")]
    [InlineData(WebSearchProvider.DuckDuckGo, IntegrationAuthKind.HeaderKey, "x-api-key")]
    public void ProviderKeysUseCredentialBindingsAndAreClearedFromTheEditor(WebSearchProvider engine, IntegrationAuthKind kind, string target) => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), new SetupSearchService(), registry, connector);
        search.SelectedProvider = HostedSearchProviders.Find(engine); search.Enabled = true; search.PendingApiKey = "test-private-key";
        search.UseKeyCommand.Execute(null);
        SettingsUntil(() => !search.IsBusy, "key saved");
        Assert.Equal("test-private-key", connector.Token);
        Assert.Null(search.PendingApiKey);
        var record = Assert.Single(registry.ListAsync().Result);
        Assert.Equal(kind, record.Authentication.Kind);
        Assert.Equal(target, Assert.Single(record.Authentication.Secrets).Target);
        Assert.DoesNotContain("test-private-key", System.Text.Json.JsonSerializer.Serialize(record));
        Assert.DoesNotContain("test-private-key", System.Text.Json.JsonSerializer.Serialize(settings.LoadAsync().Result));
    });
    [Fact]
    public void SearchRequiresOptInAndSavesTheSelectedEngineAndCloudPermissions() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var service = new SetupSearchService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), service, registry);
        SettingsWait(search.InitializeAsync());
        Assert.False(search.Enabled);
        Assert.True(search.CanContinue);
        Assert.False(search.TestCommand.CanExecute(null));
        Assert.True(settings.LoadAsync().Result.Privacy.LocalOnly);
        search.Enabled = true; search.SelectedProvider = HostedSearchProviders.Find(WebSearchProvider.Tavily);
        SettingsWait(search.ApplyAsync());
        var saved = settings.LoadAsync().Result;
        Assert.True(saved.WebSearch.Enabled);
        Assert.Equal(WebSearchProvider.Tavily, saved.WebSearch.Provider);
        Assert.True(saved.Permissions.ExternalSearch);
        Assert.False(saved.Privacy.LocalOnly);
        Assert.Equal(0, service.Searches);
        search.TestCommand.Execute(null);
        SettingsUntil(() => !search.IsBusy, "search test completed");
        Assert.Equal(1, service.Searches);
        Assert.Contains("Tavily", search.Status);
        search.Enabled = false; SettingsWait(search.ApplyAsync());
        Assert.False(settings.LoadAsync().Result.WebSearch.Enabled);
    });

    [Fact]
    public void LeavingSearchOffPreservesLocalOnlyAndDoesNotConnect() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance));
        SettingsWait(search.ApplyAsync());
        Assert.True(settings.LoadAsync().Result.Privacy.LocalOnly);
        Assert.False(settings.LoadAsync().Result.Permissions.ExternalSearch);
        Assert.False(settings.LoadAsync().Result.WebSearch.Enabled);
    });

    [Fact]
    public void DuckDuckGoRequiresSignInAndAnExplicitSearchOptIn() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var connector = new SetupRecordingConnector(registry);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), new SetupSearchService(), registry, connector);
        search.SelectedProvider = HostedSearchProviders.Find(WebSearchProvider.DuckDuckGo);
        Assert.False(search.ConnectCommand.CanExecute(null));
        search.Enabled = true;
        Assert.False(search.CanContinue);
        Assert.False(search.TestCommand.CanExecute(null));
        search.ConnectCommand.Execute(null);
        SettingsUntil(() => !search.IsBusy, "HasData sign-in completed");
        Assert.Equal("https://mcp.hasdata.com/mcp?apis=duckduckgo", Assert.Single(connector.Connects).Endpoint);
        Assert.True(search.CanContinue);
        Assert.True(search.TestCommand.CanExecute(null));
    });

    [Fact]
    public void SearchSignInCanBeCanceledAndOptionalSearchCanThenBeSkipped() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), new SetupSearchService(), registry, new WaitingSignInConnector());
        using var kit = new SetupKit(settings, search: search);
        search.SelectedProvider = HostedSearchProviders.Find(WebSearchProvider.DuckDuckGo); search.Enabled = true;
        search.ConnectCommand.Execute(null);
        SettingsUntil(() => search.HasSignInLink, "search sign-in link ready");
        Assert.True(kit.Setup.IsBusy);
        Assert.False(kit.Setup.CanContinueSearch);
        kit.Setup.CancelCommand.Execute(null);
        SettingsUntil(() => !search.IsBusy, "search sign-in canceled");
        search.Enabled = false;
        Assert.True(kit.Setup.CanContinueSearch);
        Assert.False(settings.LoadAsync().Result.Ui.FirstRunCompleted);
    });

    [Fact]
    public void SearchPageHasAllProviderIconsAndNoBindingErrorsInOnboardingAndSettings() => RunSta(() => WithTheme(() =>
    {
        var settings = new InMemorySettingsService();
        var registry = new InstalledIntegrationRegistry(new SetupConnectionStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        using var search = new SearchSetupViewModel(settings, new AppEventBus(NullLogger<AppEventBus>.Instance), new SetupSearchService(), registry);
        using var kit = new SetupKit(settings, search: search);
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        using var errors = BindingErrors.Listen();
        try
        {
            window.ShowStep(OnboardingWindow.SearchStepIndex); window.Show(); Pump(); window.UpdateLayout();
            var page = Assert.Single(Descendants<SearchSetupControl>(window));
            Assert.Equal(3, Descendants<Image>(page).Count(image => image.Source is not null));
            Assert.False(search.Enabled);
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-search-off.png");
            search.Enabled = true; Pump(); window.UpdateLayout();
            RenderFixture((FrameworkElement)window.Content, "onboarding-search-enabled.png");
            search.SelectedProvider = HostedSearchProviders.Find(WebSearchProvider.DuckDuckGo); Pump(); window.UpdateLayout();
            Assert.False(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-search-duckduckgo.png");
            Assert.Empty(errors.Messages);
        }
        finally { window.Close(); }
        var settingsKit = CreateSettingsKit(service: settings, setup: kit.Setup);
        var (settingsWindow, _, _) = CreateSettingsWindow(settingsKit);
        try
        {
            settingsKit.Model.SelectedSection = settingsKit.Model.Sections.Single(item => item.Section == SettingsSection.Integrations);
            settingsWindow.Show(); Pump(); settingsWindow.UpdateLayout();
            Assert.Single(Descendants<SearchSetupControl>(settingsWindow), page => page.IsVisible);
            RenderFixture(Named<Grid>(settingsWindow, "Root"), "settings-web-search.png");
            Assert.Empty(errors.Messages);
        }
        finally { settingsWindow.CloseForGood(); settingsKit.Model.Dispose(); }
    }));

    private sealed class SetupSearchService : IWebSearchService
    {
        public int Searches { get; private set; }
        public WebSearchSettings Saved { get; private set; } = new();
        public Task ConfigureAsync(WebSearchSettings settings, CancellationToken cancellationToken = default) { Saved = settings; return Task.CompletedTask; }
        public Task<WebSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Searches++;
            return Task.FromResult(new WebSearchResponse(HostedSearchProviders.Find(Saved.Provider).Name,
                new(false, [new(McpContentKind.Text, "A source result", null, null, null)], null)));
        }
    }
}
