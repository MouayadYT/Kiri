using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Storage;
using Assistant.Data;
using Assistant.Tools;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap;
using Assistant.UI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// How the app puts the activity log together (PROJECT_SPEC §4.8, §4.9, step 117): one log behind the agent loop, the integration code, the Settings page and the panel in the
/// conversation, kept in the app's database, with a step for every tool the Assistant has and words for it, and the integration code recording what it does.
/// </summary>
public sealed class ActivityWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-activity-" + Guid.NewGuid().ToString("N"));

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

    private IHost Host(IModelService? model = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        if (model is not null)
        {
            builder.Services.AddSingleton(model);
        }

        return builder.Build();
    }

    [Fact]
    public void OneLogIsBehindTheAgentLoopTheIntegrationCodeThePageAndThePanel_AndItIsKeptInTheDatabase()
    {
        using var host = Host();

        var log = host.Services.GetRequiredService<AuditLog>();

        Assert.Same(log, host.Services.GetRequiredService<IAgentTaskLog>());
        Assert.Same(log, host.Services.GetRequiredService<IAuditTrail>());
        Assert.Same(log, host.Services.GetRequiredService<IAuditHistory>());
        Assert.IsType<SqliteAuditStore>(host.Services.GetRequiredService<IAuditStore>());
    }

    [Fact]
    public void ThePageIsBuiltOverTheLog()
    {
        using var host = Host();

        var model = host.Services.GetRequiredService<SettingsViewModel>();

        Assert.True(model.Activity.HasHistory);
        Assert.Contains(model.Sections, section => section.Section == SettingsSection.Activity);
    }

    [Fact]
    public void WhatTheAssistantDoesWithIntegrationsIsRecorded_ByTheOnesTheAppBuilds()
    {
        using var host = Host();

        Assert.IsType<AuditedIntegrationFinder>(host.Services.GetRequiredService<IIntegrationFinder>());
        Assert.IsType<AuditedIntegrationInstaller>(host.Services.GetRequiredService<IIntegrationInstaller>());
        Assert.IsAssignableFrom<IIntegrationOffers>(host.Services.GetRequiredService<IIntegrationOffers>());
        Assert.IsType<AuditedIntegrationManager>(host.Services.GetRequiredService<IIntegrationManager>());
    }

    [Fact]
    public void EveryToolTheAssistantHasHasWordsInTheLog_AndTheLogHasWordsForNoToolItDoesNotHave()
    {
        using var host = Host();
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        var names = registry.Tools.Select(tool => tool.Name).Order().ToList();

        Assert.All(names, name => Assert.True(AuditText.HasPhrase(name), $"{name} has no words in the activity log"));
        Assert.Equal(names, AuditText.KnownTools.Order());
    }

    // The model says to calculate twice and then answers: two steps, through the real loop, executor, tools and log, kept in the real database.
    [Fact]
    public async Task ARunThroughTheRealLoopIsInTheLogAndInTheDatabase_WithNothingOfWhatItWasGivenOrReturned()
    {
        using var host = Host(new CalculatingModel());
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();
        var log = host.Services.GetRequiredService<AuditLog>();
        var session = ConversationSession.Start(TimeProvider.System);

        await foreach (var chunk in orchestrator.AskAsync(session, "work out two sums for my secret budget"))
        {
            _ = chunk;
        }

        await log.FlushAsync();
        var task = Assert.Single(await log.ListAsync(10)).Task!;
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(["calculate", "calculate"], task.Steps.Select(step => step.Name));
        Assert.Equal(["Calculate", "Calculate"], task.Steps.Select(step => step.Summary));
        Assert.All(task.Steps, step => Assert.Equal(AuditStatus.Succeeded, step.Status));
        Assert.Equal(session.Conversation.Id, task.ConversationId);

        // What was kept is what was in memory, and none of it is what the user asked or what the tool worked out.
        var kept = Assert.Single(await host.Services.GetRequiredService<IAuditStore>().ListAsync(10)).Task!;
        Assert.Equal(task.Id, kept.Id);
        Assert.Equal(2, kept.Steps.Count);
        var everything = System.Text.Json.JsonSerializer.Serialize(kept);
        Assert.DoesNotContain("secret budget", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("7*8", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("expression", everything, StringComparison.Ordinal);
    }

    private sealed class CalculatingModel : IModelService
    {
        private int _round;

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            switch (_round++)
            {
                case 0:
                    yield return AssistantResponseChunk.ForToolCall(new ToolCall("c1", "calculate", """{"expression":"7*8+12"}"""));
                    break;
                case 1:
                    yield return AssistantResponseChunk.ForToolCall(new ToolCall("c2", "calculate", """{"expression":"3*5"}"""));
                    break;
                default:
                    yield return AssistantResponseChunk.ForTextDelta("Both are done.");
                    break;
            }

            await Task.CompletedTask;
        }
    }
}
