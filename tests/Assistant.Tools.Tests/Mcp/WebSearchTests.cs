using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Search;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

public sealed class WebSearchTests
{
    [Fact]
    public void PlainTextQuotaFailuresCannotBecomeSuccessfulSearchResults()
    {
        var result = WebSearchService.NormalizeResult(Sample.Text("You've hit Exa's free MCP rate limit. Create a key to continue."));
        Assert.True(result.IsError);
        Assert.False(WebSearchService.NormalizeResult(Sample.Text("Title: Current facts\nURL: https://example.com")).IsError);
    }
    [Theory]
    [InlineData(WebSearchProvider.Exa, "query")]
    [InlineData(WebSearchProvider.Tavily, "query")]
    [InlineData(WebSearchProvider.DuckDuckGo, "q")]
    public async Task SearchesOnlyTheSelectedEngineAndReturnsItsUntrustedSourceData(WebSearchProvider engine, string queryName)
    {
        var provider = HostedSearchProviders.Find(engine);
        await using var apps = new ConnectedAppsFixture([], clients: _ =>
        {
            var client = new StubMcpClient();
            client.Tools.Add(Sample.Tool(provider.ToolName, "Searches the web.", """{"type":"object","properties":{"query":{"type":"string"},"q":{"type":"string"}}}"""));
            client.OnCall = (_, _, _) => Task.FromResult(Sample.Text("A source: https://example.com/current"));
            return client;
        });
        apps.Settings.Current = Enabled(engine);
        var service = new WebSearchService(apps.Settings, apps.Integrations, apps.Manager);
        if (provider.NeedsSignIn) await apps.Integrations.AddAsync(new InstalledIntegration
        {
            Id = provider.IntegrationId, Name = provider.Name, Transport = provider.Transport, Enabled = true,
            Permissions = provider.Permissions, Authentication = new() { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.Ready },
        });
        await service.ConfigureAsync(apps.Settings.Current.WebSearch);
        var tool = new SearchWebTool(apps.Settings, service);
        var context = new ToolContext(Guid.NewGuid(), "What happened today?");
        await tool.PrepareAsync(context, default);
        Assert.True(tool.IsOffered(context));
        var result = await tool.RunAsync(new("one", "search_web", "{}"), JsonSerializer.SerializeToElement(new { query = "a public query" }), context, default);
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Contains("connected_app", result.OutputJson);
        Assert.Contains("https://example.com/current", result.OutputJson);
        var call = Assert.Single(Assert.Single(apps.Clients.Created).Calls);
        Assert.Equal(provider.ToolName, call.Tool);
        using var arguments = JsonDocument.Parse(call.Arguments);
        Assert.Equal("a public query", arguments.RootElement.GetProperty(queryName).GetString());
        Assert.Single(await apps.Integrations.ListAsync(), record => record.Enabled);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task DisabledSearchPrivacyOrPermissionSendsNothing(bool enabled, bool localOnly, bool permission)
    {
        await using var apps = new ConnectedAppsFixture([]);
        apps.Settings.Current = Enabled(WebSearchProvider.Exa) with
        {
            WebSearch = new() { Enabled = enabled }, Privacy = new() { LocalOnly = localOnly }, Permissions = new() { ExternalSearch = permission },
        };
        var service = new WebSearchService(apps.Settings, apps.Integrations, apps.Manager);
        var tool = new SearchWebTool(apps.Settings, service);
        var context = new ToolContext(Guid.NewGuid());
        await tool.PrepareAsync(context, default);
        Assert.False(tool.IsOffered(context));
        var exception = await Assert.ThrowsAsync<McpException>(() => service.SearchAsync("public query"));
        Assert.Equal(McpFailure.Blocked, exception.Failure);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ChangingEngineDisablesThePreviousEngineAndTurningSearchOffDisablesAllOwnedRecords()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        var service = new WebSearchService(apps.Settings, apps.Integrations, apps.Manager);
        await service.ConfigureAsync(new() { Enabled = true });
        await service.ConfigureAsync(new() { Enabled = true, Provider = WebSearchProvider.Tavily });
        Assert.False((await apps.Integrations.GetAsync("websearchexa"))!.Enabled);
        Assert.True((await apps.Integrations.GetAsync("websearchtavily"))!.Enabled);
        await service.ConfigureAsync(new());
        Assert.Single(await apps.Integrations.ListAsync(), record => record.Enabled && record.Id == "todoist");
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task PrivacyIsRecheckedAfterLoadingTheRemoteCatalog()
    {
        ConnectedAppsFixture? apps = null;
        apps = new ConnectedAppsFixture([], clients: _ =>
        {
            var client = new StubMcpClient { BeforeConnect = () => { apps!.Settings.Current = apps.Settings.Current with { Privacy = new() { LocalOnly = true } }; return Task.CompletedTask; } };
            client.Tools.Add(Sample.Tool("web_search_exa", "Search", """{"type":"object"}"""));
            return client;
        });
        await using (apps)
        {
            apps.Settings.Current = Enabled(WebSearchProvider.Exa);
            var service = new WebSearchService(apps.Settings, apps.Integrations, apps.Manager);
            await service.ConfigureAsync(apps.Settings.Current.WebSearch);
            await Assert.ThrowsAsync<McpException>(() => service.SearchAsync("public query"));
            Assert.Empty(Assert.Single(apps.Clients.Created).Calls);
        }
    }

    private static AppSettings Enabled(WebSearchProvider engine) => new()
    {
        WebSearch = new() { Enabled = true, Provider = engine }, Privacy = new() { LocalOnly = false }, Permissions = new() { ExternalSearch = true },
    };
}
