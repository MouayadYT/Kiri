using System.IO;
using System.Linq;
using Assistant.Core.Agent;
using Assistant.Core.Contracts;
using Assistant.Core.Storage;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>How the app puts the agent loop together (PROJECT_SPEC section 4.8, step 114): its trace is kept in memory apart from the conversation, and the budget leaves today's tools alone.</summary>
public sealed class AgentWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-agent-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void TheAppKeepsTheTracesOfItsAgentRunsInMemory_InOnePlace()
    {
        using var host = Host();

        var sink = host.Services.GetRequiredService<IAgentTraceSink>();

        Assert.IsType<AgentTraceStore>(sink);
        Assert.Same(sink, host.Services.GetRequiredService<AgentTraceStore>());
        Assert.IsAssignableFrom<IAssistantOrchestrator>(host.Services.GetRequiredService<IAssistantOrchestrator>());
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("find the milestone report")]
    [InlineData("set the volume to 30 and take a screenshot")]
    [InlineData("what is on my calendar tomorrow")]
    public void TheDefaultBudgetLeavesTheBuiltInToolsAlone(string request)
    {
        using var host = Host();
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), request);
        var offered = registry.ToolsFor(context);

        var chosen = new BudgetedToolSelector().Select(context, offered);

        Assert.True(
            chosen.Count == offered.Count,
            $"{offered.Count} tools, {offered.Sum(AgentToolBudget.Cost)} characters; budget {AgentToolBudget.Default}");
    }
}
