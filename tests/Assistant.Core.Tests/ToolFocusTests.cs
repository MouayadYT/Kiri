using Assistant.Core.Agent;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// When not every tool can be offered, the ones the request is about are kept first. In 0.1.142 a "yes" to a message shared no word with any tool, the cut
/// fell in the order the tools were registered, and send_message (registered after the tools that are always there) was the one left out.
/// </summary>
public sealed class ToolFocusTests
{
    private static ToolDefinition Tool(string name, int size) =>
        new(name, "Does " + new string('x', Math.Max(1, size - name.Length - 30)) + ".", """{"type":"object"}""", RiskLevel.ReadOnly);

    // Fourteen tools that are always there, as the app has, and the three of a conversation about a message, last.
    private static readonly ToolDefinition[] AlwaysThere = [.. Enumerable.Range(1, 14).Select(index => Tool($"always_{index:00}", 410))];
    private static readonly ToolDefinition Draft = Tool("draft_message", 1400);
    private static readonly ToolDefinition Send = Tool("send_message", 1050);
    private static readonly ToolDefinition RememberPerson = Tool("remember_person", 1090);
    private static readonly ToolDefinition[] All = [.. AlwaysThere, Draft, Send, RememberPerson];

    [Fact]
    public void AReplyThatSharesNoWordWithAnyToolStillKeepsTheToolsTheConversationIsAbout()
    {
        var selector = new BudgetedToolSelector();
        var yes = new ToolContext(Guid.NewGuid(), "yes");

        var kept = selector.Select(yes, All, new HashSet<string>(["draft_message", "send_message", "remember_person"]));

        Assert.Contains(Send, kept);
        Assert.Contains(Draft, kept);
        Assert.Contains(RememberPerson, kept);
        Assert.True(kept.Sum(AgentToolBudget.Cost) <= AgentToolBudget.Default.MaxSchemaCharacters);
        Assert.True(kept.Count <= AgentToolBudget.Default.MaxTools);

        // Still in the order they came in.
        Assert.Equal(All.Where(kept.Contains), kept);

        // Without being told which, the cut falls in the order they came in, as it did.
        Assert.DoesNotContain(Send, selector.Select(yes, All));
    }

    [Fact]
    public void ToolsThatShareWordsWithTheRequestComeAfterTheFocusedOnes_AndEverythingIsOfferedWhileItFits()
    {
        var selector = new BudgetedToolSelector();
        var few = new[] { Tool("search_files", 600), Send };

        Assert.Equal(few, selector.Select(new ToolContext(Guid.NewGuid(), "hi"), few, new HashSet<string>()));

        var budget = new BudgetedToolSelector(new AgentToolBudget(MaxTools: 2));
        var three = new[] { Tool("search_files", 300), Tool("open_application", 300), Send };
        Assert.Equal(["search_files", "send_message"], budget.Select(new ToolContext(Guid.NewGuid(), "search my files"), three, new HashSet<string>(["send_message"])).Select(tool => tool.Name));
    }

    private sealed class FocusingRegistry(IReadOnlyList<ToolDefinition> tools, IReadOnlySet<string> focused) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Tools => tools;

        public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context) => tools;

        public IReadOnlySet<string> FocusedFor(ToolContext context) => focused;

        public ToolDefinition? Find(string name) => tools.FirstOrDefault(tool => tool.Name == name);
    }

    [Fact]
    public async Task TheRunAsksTheRegistryWhichToolsTheRequestIsAbout_AndOffersThemToTheModel()
    {
        var model = new SequencedModel(new ModelInfo("test-model", 8192) { SupportsToolCalling = true }, [AssistantResponseChunk.ForTextDelta("Here it is.")]);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(DateTimeOffset.UnixEpoch), NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: new FocusingRegistry(All, new HashSet<string>(["send_message", "remember_person"])), toolExecutor: new FakeToolExecutor("{}"));

        await foreach (var _ in orchestrator.AskAsync(ConversationSession.Start(new TestClock(DateTimeOffset.UnixEpoch)), "yes"))
        {
        }

        var offered = Assert.Single(model.Requests).Tools;
        Assert.Contains(Send, offered);
        Assert.Contains(RememberPerson, offered);
    }
}
