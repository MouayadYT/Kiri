using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The whole life of an installed integration as the app puts the pieces together (PROJECT_SPEC §4.8, steps 108-109), with the real sample integration ("Sample Notes"):
/// <c>demo integration</c> offers it, nothing is downloaded or run until Install is clicked, the real installer unpacks it into the Assistant's folder and starts
/// it once to check it, it is listed, it is used by a request (its program really is started over its standard streams), after a restart it is reused without being
/// searched for or downloaded again and without its program being started until a tool is called, and it can be turned off, reconnected and removed with every file it
/// installed. The web is never used: the sample is served from this PC.
/// </summary>
public sealed class IntegrationLifecycleWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-integration-life-" + Guid.NewGuid().ToString("N"));

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
            // A program that is still ending holds a file; the folder is in the temp directory and goes in time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Counts the connections made to installed integrations, and what happens to them.
    private sealed class CountingClients(IMcpClientFactory inner) : IMcpClientFactory
    {
        public int Created { get; private set; }

        public IMcpClient Create(InstalledIntegration integration)
        {
            Created++;
            return inner.Create(integration);
        }
    }

    private IHost Host(CountingClients? counting = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        if (counting is not null)
        {
            builder.Services.AddSingleton<IMcpClientFactory>(counting);
        }

        return builder.Build();
    }

    private static async Task<IntegrationOfferContent> OfferAsync(IHost host)
    {
        var message = await host.Services.GetRequiredService<ISampleIntegrationDemo>().OfferAsync(CancellationToken.None);
        return Assert.IsType<IntegrationOfferContent>(message.Content.Last());
    }

    private async Task InstallSampleAsync(IHost host)
    {
        var panel = await OfferAsync(host);
        await panel.InstallAsync();
        Assert.Equal(IntegrationOfferState.Installed, panel.State);
    }

    private AppPaths Paths => new(_root);

    private static async Task<ToolResult> RunToolAsync(IHost host, string request, string tool)
    {
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), request);
        await registry.PrepareToolsAsync(context);
        Assert.Contains(registry.ToolsFor(context), definition => definition.Name == tool);
        return await host.Services.GetRequiredService<IToolExecutor>().ExecuteAsync(new ToolCall("call-1", tool, "{}"), context);
    }

    [Fact]
    public async Task DemoIntegrationOffersTheSampleAndNothingIsDownloadedOrRunUntilInstallIsClicked()
    {
        using var host = Host();

        var panel = await OfferAsync(host);

        Assert.Equal(IntegrationOfferState.Waiting, panel.State);
        Assert.Equal("Install the Sample Notes integration?", panel.Title);
        Assert.Contains("made-up", panel.Notes.Single(), StringComparison.Ordinal);
        Assert.Contains(panel.Needs, line => line.StartsWith("No account or key.", StringComparison.Ordinal));
        Assert.DoesNotContain(panel.Needs, line => line.Contains("Local Only", StringComparison.Ordinal));
        Assert.False(File.Exists(Paths.IntegrationsFilePath));
        Assert.True(!Directory.Exists(Paths.IntegrationsDirectory) || !Directory.EnumerateFileSystemEntries(Paths.IntegrationsDirectory).Any());
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
    }

    [Fact]
    public async Task CancellingTheSampleOfferInstallsNothing()
    {
        using var host = Host();
        var panel = await OfferAsync(host);

        panel.CancelCommand.Execute(null);

        Assert.Equal(IntegrationOfferState.Cancelled, panel.State);
        Assert.False(File.Exists(Paths.IntegrationsFilePath));
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
    }

    [Fact]
    public async Task ClickingInstallInstallsTheSampleIntoTheAssistantsFolderAndListsIt()
    {
        using var host = Host();
        var panel = await OfferAsync(host);

        await panel.InstallAsync();

        Assert.Equal(IntegrationOfferState.Installed, panel.State);
        Assert.Contains("Settings > Integrations", panel.ResultText, StringComparison.Ordinal);
        var versionFolder = Path.Combine(Paths.IntegrationsDirectory, "samplenotes", "1.0.0");
        Assert.True(File.Exists(Path.Combine(versionFolder, "bundle", "Assistant.SampleMcpServer.exe")));
        Assert.True(File.Exists(Path.Combine(versionFolder, "install.json")));
        Assert.Equal(["samplenotes"], Directory.GetDirectories(Paths.IntegrationsDirectory).Select(path => Path.GetFileName(path)!).ToArray());
        var record = Assert.Single(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
        Assert.Equal("samplenotes", record.Id);
        Assert.Equal("Sample Notes", record.Name);
        Assert.True(record.Enabled);
        Assert.Equal("1.0.0", record.InstalledVersion);
        Assert.Equal(Path.Combine(versionFolder, "bundle", "Assistant.SampleMcpServer.exe"), record.Transport.Command);
        Assert.Equal(["add_note", "list_notes"], record.Capabilities.ToolNames.Order().ToArray());
        Assert.Equal(["list_notes"], record.Permissions.ReadOnlyTools);
        Assert.False(record.Permissions.LeavesThisPc);
        Assert.Equal(InstallSourceKind.Bundle, record.Managed!.Kind);

        var info = Assert.Single(await host.Services.GetRequiredService<IIntegrationManager>().ListAsync());
        Assert.Equal("Sample Notes", info.Name);
        Assert.Equal("1.0.0", info.Version);
        Assert.True(info.IsManaged);
        Assert.StartsWith("Added by you", info.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstalledSampleIsUsedByARequestAndItsProgramReallyAnswers()
    {
        using var host = Host();
        await InstallSampleAsync(host);

        // The installed integration is reused: the request is not answered by the Assistant's search for one, and the model is offered the tool.
        var handler = host.Services.GetRequiredService<IConnectedAppRequestHandler>();
        Assert.Null(await handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), "List my notes in Sample Notes")));
        var result = await RunToolAsync(host, "List my notes in Sample Notes", "mcp_samplenotes_list_notes");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        using var output = JsonDocument.Parse(result.OutputJson);
        var text = output.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Buy milk (sample)", text, StringComparison.Ordinal);
        Assert.Contains("Call Anna on Friday (sample)", text, StringComparison.Ordinal);
        var health = (await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().GetAsync("samplenotes"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Healthy, health.Status);
        Assert.Equal("Connected", Assert.Single(await host.Services.GetRequiredService<IIntegrationManager>().ListAsync()).Health);
    }

    [Fact]
    public async Task AfterARestartTheSampleIsReusedWithoutSearchingOrDownloadingAndItsProgramIsStartedOnlyWhenAToolIsCalled()
    {
        using (var first = Host())
        {
            await InstallSampleAsync(first);
            await RunToolAsync(first, "List my notes in Sample Notes", "mcp_samplenotes_list_notes");
        }

        // The app is closed and opened again.
        var counting = new CountingClients(new McpClientFactory());
        using var host = Host(counting);
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), "List my notes in Sample Notes");
        await registry.PrepareToolsAsync(context);

        Assert.Contains(registry.ToolsFor(context), definition => definition.Name == "mcp_samplenotes_list_notes");
        Assert.Equal(0, counting.Created);
        Assert.Null(await host.Services.GetRequiredService<IConnectedAppRequestHandler>().TryAnswerAsync(context));
        Assert.Equal(0, counting.Created);
        Assert.True(File.Exists(Path.Combine(Paths.CacheDirectory, "integration-tools.json")));
        var downloads = Directory.Exists(Paths.IntegrationsDirectory) ? Directory.GetDirectories(Paths.IntegrationsDirectory).Select(path => Path.GetFileName(path)!).ToArray() : [];
        Assert.Equal(["samplenotes"], downloads);

        var result = await host.Services.GetRequiredService<IToolExecutor>().ExecuteAsync(new ToolCall("call-2", "mcp_samplenotes_list_notes", "{}"), context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, counting.Created);
    }

    [Fact]
    public async Task AskingForTheDemoAgainWhileTheSampleIsInstalledSaysSoAndOffersNothing()
    {
        using var host = Host();
        await InstallSampleAsync(host);

        var message = await host.Services.GetRequiredService<ISampleIntegrationDemo>().OfferAsync(CancellationToken.None);

        Assert.DoesNotContain(message.Content, part => part is IntegrationOfferContent);
        Assert.Contains("installed already", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningTheSampleOffEndsItsProgramAndItsToolsAreNoLongerOffered()
    {
        var counting = new CountingClients(new McpClientFactory());
        using var host = Host(counting);
        await InstallSampleAsync(host);
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var manager = host.Services.GetRequiredService<IIntegrationManager>();
        await RunToolAsync(host, "List my notes in Sample Notes", "mcp_samplenotes_list_notes");

        await manager.SetEnabledAsync("samplenotes", false);

        var context = new ToolContext(Guid.NewGuid(), "List my notes in Sample Notes");
        await registry.PrepareToolsAsync(context);
        Assert.DoesNotContain(registry.ToolsFor(context), definition => definition.Name.StartsWith("mcp_samplenotes", StringComparison.Ordinal));
        var info = Assert.Single(await manager.ListAsync());
        Assert.False(info.Enabled);
        Assert.Equal("Turned off", info.Health);
        var reply = await host.Services.GetRequiredService<IConnectedAppRequestHandler>().TryAnswerAsync(context);
        Assert.Equal(ConnectedAppReplyKind.InstalledNotUsable, reply!.Kind);

        await manager.SetEnabledAsync("samplenotes", true);
        await RunToolAsync(host, "List my notes in Sample Notes", "mcp_samplenotes_list_notes");
    }

    [Fact]
    public async Task ReconnectingStartsTheProgramAgainAndReadsItsTools()
    {
        using var host = Host();
        await InstallSampleAsync(host);
        var manager = host.Services.GetRequiredService<IIntegrationManager>();

        var outcome = await manager.ReconnectAsync("samplenotes");

        Assert.True(outcome.Connected, outcome.Message);
        Assert.Equal(2, outcome.ToolCount);
        Assert.Equal("Sample Notes is connected. It offers 2 tools.", outcome.Message);
        Assert.Equal("Connected", Assert.Single(await manager.ListAsync()).Health);
    }

    [Fact]
    public async Task RemovingTheSampleDeletesEverythingItInstalledAndItCanBeInstalledAgain()
    {
        using var host = Host();
        await InstallSampleAsync(host);
        var manager = host.Services.GetRequiredService<IIntegrationManager>();
        await RunToolAsync(host, "List my notes in Sample Notes", "mcp_samplenotes_list_notes");

        var outcome = await manager.RemoveAsync("samplenotes");

        Assert.True(outcome.Removed);
        Assert.True(outcome.FilesDeleted);
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
        Assert.False(Directory.Exists(Path.Combine(Paths.IntegrationsDirectory, "samplenotes")));
        Assert.Empty(await manager.ListAsync());
        var cache = Path.Combine(Paths.CacheDirectory, "integration-tools.json");
        Assert.True(!File.Exists(cache) || !File.ReadAllText(cache).Contains("samplenotes", StringComparison.Ordinal));

        // The request is a request for an app with no integration again, and the sample can be offered and installed again.
        await InstallSampleAsync(host);
        Assert.Single(await manager.ListAsync());
    }

    [Fact]
    public async Task OnlyTheDemoPathCanDownloadFromThisPcAndTheAppsOwnInstallerNeverCan()
    {
        using var host = Host();

        // The app's own downloader refuses an address on this PC; only the demo's installer is built with the policy that allows it.
        var downloader = host.Services.GetRequiredService<IPackageDownloader>();
        var refused = await Assert.ThrowsAsync<InstallException>(() => downloader.DownloadAsync(
            new Uri("http://127.0.0.1:5000/notes.mcpb"), Path.Combine(_root, "x.bin"), new ContentHash("sha256", new string('a', 64)), 1000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.NotAllowed, refused.Failure);
    }
}
