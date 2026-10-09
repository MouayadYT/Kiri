using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Storage;
using Assistant.Core.Tools;
using Assistant.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>How the app puts the connected apps together (PROJECT_SPEC section 4.8, step 104): nothing is read or connected to until a request needs it, and what is kept is in the app's own folder.</summary>
public sealed class ConnectedAppsWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-connected-apps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    private IHost Host()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        return builder.Build();
    }

    private static InstalledIntegration Remote(string endpoint) => new()
    {
        Id = "todoist",
        Name = "Todoist",
        Transport = new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = endpoint },
        Enabled = true,
    };

    [Fact]
    public void TheAppHasTheRegistryTheSourceAndTheToolsThatUseThem()
    {
        using var host = Host();
        var services = host.Services;

        Assert.NotNull(services.GetRequiredService<IInstalledIntegrationRegistry>());
        Assert.NotNull(services.GetRequiredService<IInstalledIntegrationStore>());
        Assert.NotNull(services.GetRequiredService<IMcpClientFactory>());
        Assert.NotNull(services.GetRequiredService<IDynamicToolSource>());
        var connections = services.GetRequiredService<Assistant.UI.Onboarding.ConnectionsSetupViewModel>();
        Assert.True(connections.IsAvailable);
        Assert.Same(connections, services.GetRequiredService<Assistant.UI.Onboarding.SetupViewModel>().Connections);
        var search = services.GetRequiredService<Assistant.UI.Onboarding.SearchSetupViewModel>();
        Assert.True(search.IsAvailable);
        Assert.Same(search, services.GetRequiredService<Assistant.UI.Onboarding.SetupViewModel>().Search);
        Assert.Same(services.GetRequiredService<IInstalledIntegrationRegistry>(), services.GetRequiredService<IInstalledIntegrationRegistry>());
        Assert.Same(services.GetRequiredService<IDynamicToolSource>(), services.GetRequiredService<IDynamicToolSource>());
    }

    [Fact]
    public async Task NothingIsReadOrWrittenUntilAnIntegrationIsInstalled()
    {
        using var host = Host();
        var tools = host.Services.GetRequiredService<IToolRegistry>();

        await tools.PrepareToolsAsync(new ToolContext(Guid.NewGuid(), "add milk to my todoist"));
        var offered = tools.ToolsFor(new ToolContext(Guid.NewGuid(), "add milk to my todoist"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.False(File.Exists(host.Services.GetRequiredService<AppPaths>().IntegrationsFilePath));
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
    }

    [Fact]
    public async Task AnIntegrationIsKeptInTheAppsOwnFolderAndSurvivesARestart()
    {
        string file;
        using (var first = Host())
        {
            await first.Services.GetRequiredService<IInstalledIntegrationRegistry>().AddAsync(Remote("https://mcp.example.com/mcp"));
            file = first.Services.GetRequiredService<AppPaths>().IntegrationsFilePath;
            Assert.Equal(Path.Combine(_root, "integrations.json"), file);
            Assert.True(File.Exists(file));
        }

        using var second = Host();
        var installed = Assert.Single(await second.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
        Assert.Equal("todoist", installed.Id);
    }

    [Fact]
    public async Task WhileLocalOnlyIsOnAnAppOverTheNetworkIsNotConnectedToWhateverTheRequestSays()
    {
        using var host = Host();
        var registry = host.Services.GetRequiredService<IInstalledIntegrationRegistry>();
        await registry.AddAsync(Remote("https://mcp.example.com/mcp"));
        var tools = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), "add milk to my todoist");

        // Local Only is on by default (Settings, Privacy), so nothing is sent: not even to find out whether the server is there.
        await tools.PrepareToolsAsync(context);

        Assert.DoesNotContain(tools.ToolsFor(context), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        var integration = (await registry.GetAsync("todoist"))!;
        Assert.Equal(IntegrationHealthStatus.Unknown, integration.Health.Status);
        Assert.Null(integration.Health.CheckedAt);
    }

    [Fact]
    public async Task AnAppOnThisPcThatIsNotThereIsLeftOutAndItsHealthIsRecordedInTheFile()
    {
        using var host = Host();
        var registry = host.Services.GetRequiredService<IInstalledIntegrationRegistry>();

        // Nothing listens on port 1; the app is on this PC, so Local Only does not stop the attempt.
        await registry.AddAsync(Remote("http://127.0.0.1:1/mcp"));
        var tools = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), "add milk to my todoist");

        await tools.PrepareToolsAsync(context);

        Assert.DoesNotContain(tools.ToolsFor(context), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        var health = (await registry.GetAsync("todoist"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Unreachable, health.Status);
        Assert.Equal(McpFailure.ConnectFailed, health.Failure);
        Assert.Contains("unreachable", await File.ReadAllTextAsync(host.Services.GetRequiredService<AppPaths>().IntegrationsFilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANameThatWasNeverOfferedIsNotAToolInTheAppsExecutor()
    {
        using var host = Host();
        var executor = host.Services.GetRequiredService<IToolExecutor>();

        var result = await executor.ExecuteAsync(new ToolCall("c1", "mcp_todoist_create_task", """{"task_title":"x"}"""), new ToolContext(Guid.NewGuid(), "add a task to todoist"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
    }

    [Fact]
    public void TheBuiltInCatalogIsTheSameAsBefore()
    {
        using var host = Host();
        var names = host.Services.GetRequiredService<IToolRegistry>().Tools.Select(tool => tool.Name).ToList();

        Assert.DoesNotContain(names, name => ConnectedAppTools.IsConnectedAppTool(name));
        Assert.Contains("calculate", names);
        Assert.Contains("search_files", names);
    }
}
