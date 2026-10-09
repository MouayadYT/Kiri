using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// A request for an external app that has no integration, as the app puts the pieces together (PROJECT_SPEC section 4.8, steps 105-106): the real
/// orchestrator, resolver and finder, the real settings file in a temporary folder and a model that records whether it was asked. With the default settings
/// (Local Only on) nothing is sent and the Assistant says what it would need; the model is not asked and does not get to pretend.
/// </summary>
public sealed class ExternalAppRequestsWiringTests : IDisposable
{
    // An app with no known server of its own, so that it is looked for; Microsoft To Do, Todoist and the others on the list are connected instead (below).
    private const string AddMilk = "Add 'buy milk' to TickTick";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-external-app-" + Guid.NewGuid().ToString("N"));

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

    private sealed class RecordingModel : IModelService
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return AssistantResponseChunk.ForTextDelta("Done! I added it to Microsoft To Do.");
        }
    }

    private sealed class FoundFinder : IIntegrationFinder
    {
        public List<IntegrationNeed> Asked { get; } = [];

        public Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
        {
            Asked.Add(need);
            return Task.FromResult(new IntegrationDiscoveryResult
            {
                Status = DiscoveryStatus.Found,
                SourcesAnswered = ["github"],
                Candidates =
                [
                    new IntegrationCandidate
                    {
                        Name = "MAG-Cie/mcp-microsoft-todo",
                        SourceUrl = "https://github.com/MAG-Cie/mcp-microsoft-todo",
                        Publisher = "MAG-Cie",
                        Trust = CandidateTrust.Community,
                        License = "MIT",
                        Evidence = CapabilityEvidence.ToolListed,
                        ToolNames = ["create_task"],
                    },
                ],
            });
        }
    }

    private IHost Host(IModelService model, Action<IServiceCollection>? more = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        builder.Services.AddSingleton(model);
        more?.Invoke(builder.Services);
        return builder.Build();
    }

    private static async Task<List<AssistantResponseChunk>> ReadAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    [Fact]
    public async Task WithTheDefaultSettingsTheAssistantExplainsWhatItWouldNeedAndTheModelIsNotAsked()
    {
        var model = new RecordingModel();
        using var host = Host(model);
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();
        var session = ConversationSession.Start(TimeProvider.System);

        var chunks = await ReadAsync(orchestrator.AskAsync(session, AddMilk));

        Assert.Empty(model.Requests);
        var text = Assert.Single(chunks).Text!;
        Assert.StartsWith("I can't create a task in TickTick yet: no TickTick integration is installed.", text, StringComparison.Ordinal);
        Assert.Contains("Local Only", text, StringComparison.Ordinal);
        Assert.DoesNotContain("buy milk", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], session.Conversation.Messages.Select(message => message.Role));
        Assert.Equal(text, session.Conversation.Messages[1].Text);

        // Nothing was written: no integration was installed and nothing was kept from a search that was not made.
        var paths = host.Services.GetRequiredService<AppPaths>();
        Assert.False(File.Exists(paths.IntegrationsFilePath));
        Assert.False(File.Exists(Path.Combine(paths.CacheDirectory, "integration-discovery.json")));
    }

    [Fact]
    public async Task ARequestThatIsNotForAnExternalAppIsAnsweredByTheModelAsAlways()
    {
        var model = new RecordingModel();
        using var host = Host(model);
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();

        foreach (var request in new[] { "What is the capital of France?", "How do I add a task in Microsoft To Do?", "Open Spotify", "hello" })
        {
            var chunks = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), request));
            Assert.Equal("Done! I added it to Microsoft To Do.", Assert.Single(chunks).Text);
        }

        Assert.Equal(4, model.Requests.Count);
    }

    [Fact]
    public async Task WithTheLocksOpenTheFinderIsAskedForTheAppAndTheCapabilityOnly()
    {
        var model = new RecordingModel();
        var finder = new FoundFinder();
        using var host = Host(model, services => services.AddSingleton<IIntegrationFinder>(finder));
        var settings = host.Services.GetRequiredService<ISettingsService>();
        var current = await settings.LoadAsync();
        await settings.SaveAsync(current with
        {
            Privacy = current.Privacy with { LocalOnly = false },
            Permissions = current.Permissions with { ExternalSearch = true },
        });
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();

        var chunks = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), AddMilk));

        Assert.Empty(model.Requests);
        var need = Assert.Single(finder.Asked);
        Assert.Equal("ticktick", need.AppKey);
        Assert.Equal("create task", need.Capability.Phrase);
        var text = Assert.Single(chunks).Text!;
        Assert.Contains("`MAG-Cie/mcp-microsoft-todo`", text, StringComparison.Ordinal);
        // The candidate says nothing about how it is installed, so it does not pass the review (step 107), and nothing is offered.
        Assert.Contains("None of them passed my checks", text, StringComparison.Ordinal);
        Assert.Contains("It does not say how it is installed.", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was downloaded, installed or run", text, StringComparison.Ordinal);
        Assert.DoesNotContain("buy milk", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInstalledIntegrationThatCannotBeReachedIsReportedAndNothingIsLookedFor()
    {
        var model = new RecordingModel();
        var finder = new FoundFinder();
        using var host = Host(model, services => services.AddSingleton<IIntegrationFinder>(finder));
        var registry = host.Services.GetRequiredService<IInstalledIntegrationRegistry>();

        // An address on this PC where nothing listens: the connection is refused at once.
        await registry.AddAsync(new InstalledIntegration
        {
            Id = "mstodo",
            Name = "Microsoft To Do",
            Transport = new IntegrationTransport { Kind = Assistant.Tools.Mcp.McpTransportKind.StreamableHttp, Endpoint = "http://127.0.0.1:1/mcp" },
            Enabled = true,
        });
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();

        var chunks = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), "Add 'buy milk' to Microsoft To Do"));

        // It is installed, so it is not looked for again; it cannot be reached, so the Assistant says so instead of letting the model pretend.
        Assert.Empty(finder.Asked);
        Assert.Empty(model.Requests);
        Assert.StartsWith("I can't create a task in Microsoft To Do right now:", Assert.Single(chunks).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAppCanConnectKnownAppsWithTheUsersOwnBrowserAndEveryDecoratorOfTheOffersPassesTheConnectionOn()
    {
        using var host = Host(new RecordingModel());

        Assert.IsType<Assistant.UI.Integrations.UrlLauncherOAuthBrowser>(host.Services.GetRequiredService<Assistant.Tools.Mcp.Auth.IOAuthBrowser>());
        Assert.NotNull(host.Services.GetRequiredService<IIntegrationConnector>());
        Assert.NotNull(host.Services.GetRequiredService<Assistant.Tools.Mcp.Auth.McpOAuthSessions>());

        // The offers are the real broker (through the audit and the demos' decorators), and a connection offered through it is a real offer, not a refusal.
        var offers = host.Services.GetRequiredService<IIntegrationOffers>();
        var offer = await offers.OfferConnectAsync(KnownEndpoints.For("todoist")!, null, null);
        Assert.Equal(IntegrationOfferKind.Connect, offer.Kind);
        offers.Decline(offer.OfferId);
        Assert.Equal(InstallFailure.OfferExpired, (await offers.AcceptAsync(offer.OfferId)).Failure);
    }

    [Fact]
    public async Task AKnownAppIsOfferedAConnectionNotSearchedForAndTheModelIsNotAskedWhileLocalOnlyKeepsItOut()
    {
        var model = new RecordingModel();
        var finder = new FoundFinder();
        using var host = Host(model, services => services.AddSingleton<IIntegrationFinder>(finder));
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();

        // With the defaults (Local Only on) an app reached over the internet is not offered, and nothing is searched for.
        var blocked = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), "Add 'buy milk' to Todoist"));
        Assert.Contains("Local Only mode is on", Assert.Single(blocked).Text!, StringComparison.Ordinal);
        Assert.Empty(finder.Asked);
        Assert.Empty(model.Requests);

        var settings = host.Services.GetRequiredService<ISettingsService>();
        var current = await settings.LoadAsync();
        await settings.SaveAsync(current with { Privacy = current.Privacy with { LocalOnly = false } });

        var chunks = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), "Add 'buy milk' to Microsoft To Do"));

        // The Assistant offers to connect it (the user's click opens their browser), the request is set aside for after, nothing is searched for and nothing is connected yet.
        Assert.True(chunks.Any(chunk => chunk.Offer is not null), string.Join(" | ", chunks.Select(chunk => chunk.Type + ":" + chunk.Text)));
        var offer = chunks.Select(chunk => chunk.Offer).OfType<IntegrationOffer>().Single();
        Assert.Equal(IntegrationOfferKind.Connect, offer.Kind);
        Assert.Equal("Microsoft To Do", offer.AppName);
        Assert.Contains(chunks, chunk => chunk.Pending is not null);
        Assert.Empty(finder.Asked);
        Assert.Empty(model.Requests);
        Assert.False(File.Exists(host.Services.GetRequiredService<AppPaths>().IntegrationsFilePath));
    }
}
