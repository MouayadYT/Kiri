using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>The name of a connected app's tool tells where it came from, so no built-in tool may take one.</summary>
public sealed class McpRegistryReservationTests
{
    private static HandlerTool Tool(string name) =>
        new(
            ToolDefinition.Create(name, "Does a thing.", [new ToolParameter("text", ToolParameterType.String, "Some text.")], RiskLevel.ReadOnly),
            (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}")));

    [Fact]
    public void ABuiltInToolMayNotTakeTheNameOfAConnectedAppsTool()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool("mcp_todoist_create_task")]));
        Assert.Contains(ConnectedAppTools.Prefix, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInToolWithAnotherNameIsFine()
    {
        var registry = new ToolRegistry([Tool("search_files"), Tool("my_mcp_helper")]);
        Assert.Equal(["search_files", "my_mcp_helper"], registry.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task WithNoSourceOfConnectedAppsTheRegistryAndTheExecutorKnowOnlyTheBuiltInTools()
    {
        var registry = new ToolRegistry([Tool("search_files")]);
        var context = new ToolContext(Guid.NewGuid(), "use todoist");

        await registry.PrepareToolsAsync(context);

        Assert.Equal(["search_files"], registry.ToolsFor(context).Select(tool => tool.Name));
        Assert.Null(registry.Find("mcp_todoist_create_task"));
        var result = await new ToolExecutor([Tool("search_files")]).ExecuteAsync(new ToolCall("c1", "mcp_todoist_create_task", "{}"), context);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
    }

    private sealed class RogueSource(ITool tool) : IDynamicToolSource
    {
        public Task PrepareAsync(ToolContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public IReadOnlyList<ITool> Offered(ToolContext context) => [tool];

        public ITool? Find(ToolContext context, string name) => tool;

        public ToolDefinition? Describe(string name) => tool.Definition;
    }

    [Fact]
    public async Task AToolFromASourceThatBreaksTheRulesForToolsIsNeverRunWhateverTheSourceSays()
    {
        // A name that reads as a command: the guard that holds for built-in tools holds for these too.
        var runs = 0;
        var rogue = new HandlerTool(
            new ToolDefinition("mcp_app_run_command", "Runs a command.", """{"type":"object","properties":{"text":{"type":"string"}}}""", RiskLevel.ReadOnly),
            (call, _, _, _) =>
            {
                runs++;
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            });
        var executor = new ToolExecutor([], permissions: null, policy: null, dynamicTools: new RogueSource(rogue));

        var result = await executor.ExecuteAsync(new ToolCall("c1", "mcp_app_run_command", """{"text":"x"}"""), new ToolContext(Guid.NewGuid(), "x"));

        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ADestructiveToolFromASourceIsNeverRun()
    {
        var runs = 0;
        var destructive = new HandlerTool(
            new ToolDefinition("mcp_app_wipe", "Wipes.", """{"type":"object","properties":{}}""", RiskLevel.Destructive),
            (call, _, _, _) =>
            {
                runs++;
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            });
        var executor = new ToolExecutor([], new FakeConfirmation(true), new FakePermissions(true), new RogueSource(destructive));

        var result = await executor.ExecuteAsync(new ToolCall("c1", "mcp_app_wipe", "{}"), new ToolContext(Guid.NewGuid(), "x"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.NotAllowed, code);
        Assert.Equal(0, runs);
    }
}
