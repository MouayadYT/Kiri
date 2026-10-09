using System.Runtime.CompilerServices;
using Assistant.Core.Budgeting;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.Core.People;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.AssistantOrchestratorToolTests;

namespace Assistant.Core.Tests;

/// <summary>
/// A chat that has outgrown its context window is offered more room, once; a tool call the model writes out as words is not shown as its answer; a
/// question that came with a picture says so to the tools; and a handle is read for the name and the service in it.
/// </summary>
public sealed class ChatRoomAndRestraintTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    // ---- more room for a long chat ----

    // The local model as the model service sees it: loaded with the ordinary window, or with the larger one while the chat being answered wants it.
    private sealed class WindowedModel : IModelService, IModelContextDemand
    {
        public bool Documents { get; private set; }

        public List<ModelRequest> Requests { get; } = [];

        public bool Use(bool documents)
        {
            var changed = Documents != documents;
            Documents = documents;
            return changed;
        }

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", Documents ? 16384 : 4096));

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return AssistantResponseChunk.ForTextDelta("Sure.");
            await Task.CompletedTask;
        }
    }

    private sealed class Questions(ConfirmationDecision answer) : IPermissionService
    {
        public List<(ToolDefinition Tool, ToolContext Context, ToolConfirmation Question)> Asked { get; } = [];

        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Asked.Add((tool, context, confirmation));
            return Task.FromResult(answer);
        }
    }

    private static readonly ContextLimitSettings Limits = new() { NormalContextTokens = 4096, HeavyContextTokens = 16384, ReservedOutputTokens = 512 };

    // Forty earlier messages of about a hundred words each: well past an ordinary window of 4,096 tokens, well within 16,384.
    private static ConversationSession LongChat()
    {
        var earlier = Enumerable.Range(1, 20)
            .SelectMany(number => new[]
            {
                new Message(Guid.NewGuid(), MessageRole.User, $"Question {number} " + string.Join(' ', Enumerable.Repeat("word", 100)), Start),
                new Message(Guid.NewGuid(), MessageRole.Assistant, $"Answer {number} " + string.Join(' ', Enumerable.Repeat("word", 100)), Start),
            })
            .ToArray();
        return new ConversationSession(new Conversation(Guid.NewGuid(), string.Empty, Start, Start) { Messages = earlier });
    }

    private AssistantOrchestrator Orchestrator(WindowedModel model, IPermissionService? questions, ContextLimitSettings? limits = null) =>
        new(
            model, new FixedSettings(new AppSettings { ContextLimits = limits ?? Limits }), new PromptBuilder(), new FakeImagePreprocessor(), _clock,
            NullLogger<AssistantOrchestrator>.Instance, contextDemand: model, questions: questions);

    [Fact]
    public async Task AChatThatNoLongerFitsAsksForMoreRoom_AndOnAYesItIsAnsweredWholeWithTheLargerWindow()
    {
        var model = new WindowedModel();
        var questions = new Questions(ConfirmationDecision.Approved);
        var session = LongChat();
        var orchestrator = Orchestrator(model, questions);

        var chunks = await ReadAllAsync(orchestrator.AskAsync(session, "And now?"));

        // Asked in the chat itself, with Yes and Cancel, as a question that can never be "always allowed".
        var (tool, context, question) = Assert.Single(questions.Asked);
        Assert.Equal(AssistantOrchestrator.RaiseContextQuestion, question.Title);
        Assert.Equal(("Yes", "Cancel"), (question.ApproveLabel, question.DeclineLabel));
        Assert.Equal(ConfirmationKind.Other, question.Kind);
        Assert.Equal(session.Conversation.Id, context.ConversationId);
        Assert.Equal(RiskLevel.SideEffect, tool.RiskLevel);
        Assert.Equal(["Now", "If you say yes"], question.Details.Select(detail => detail.Label));

        // Nothing was left out: every earlier message went to the model, which is loaded with the larger window.
        Assert.DoesNotContain(chunks, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning);
        Assert.Equal(41, Assert.Single(model.Requests).Messages.Count);
        Assert.True(model.Documents);

        // The next turn of the chat has the room too, and nobody is asked again.
        await ReadAllAsync(orchestrator.AskAsync(session, "And then?"));
        Assert.Single(questions.Asked);
        Assert.True(model.Documents);
        Assert.Equal(43, model.Requests[^1].Messages.Count);

        // Another chat is answered with the ordinary window.
        await ReadAllAsync(orchestrator.AskAsync(ConversationSession.Start(_clock), "Hello"));
        Assert.False(model.Documents);
    }

    [Fact]
    public async Task OnCancelTheChatGoesOnWithinItsLimit_AsBefore_AndIsNotAskedAgain()
    {
        var model = new WindowedModel();
        var questions = new Questions(ConfirmationDecision.Declined);
        var session = LongChat();
        var orchestrator = Orchestrator(model, questions);

        var chunks = await ReadAllAsync(orchestrator.AskAsync(session, "And now?"));
        await ReadAllAsync(orchestrator.AskAsync(session, "And then?"));

        Assert.Single(questions.Asked);
        Assert.Contains(chunks, chunk => chunk is { Type: AssistantResponseChunkType.ContextWarning, Text: ContextBudgetNotices.EarlierMessagesLeftOut });
        // Both turns were trimmed to fit: the earliest messages were left out.
        Assert.Equal(2, model.Requests.Count);
        Assert.True(model.Requests[0].Messages.Count < 41);
        Assert.True(model.Requests[1].Messages.Count < 43);
        Assert.False(model.Documents);
    }

    [Fact]
    public async Task AQuestionNobodySawIsAskedAgainNextTime()
    {
        var model = new WindowedModel();
        var questions = new Questions(ConfirmationDecision.CouldNotAsk);
        var session = LongChat();
        var orchestrator = Orchestrator(model, questions);

        await ReadAllAsync(orchestrator.AskAsync(session, "And now?"));
        await ReadAllAsync(orchestrator.AskAsync(session, "And then?"));

        Assert.Equal(2, questions.Asked.Count);
    }

    [Fact]
    public async Task NobodyIsAskedWhenEverythingFits_WhenThereIsNoMoreRoomToHave_OrWhenThereIsNobodyToAsk()
    {
        // A short chat fits.
        var shortChat = new Questions(ConfirmationDecision.Approved);
        await ReadAllAsync(Orchestrator(new WindowedModel(), shortChat).AskAsync(ConversationSession.Start(_clock), "Hello"));
        Assert.Empty(shortChat.Asked);

        // The limit for files is no larger than the ordinary one: more room would be none.
        var noMore = new Questions(ConfirmationDecision.Approved);
        var chunks = await ReadAllAsync(Orchestrator(new WindowedModel(), noMore, Limits with { HeavyContextTokens = 4096 }).AskAsync(LongChat(), "And now?"));
        Assert.Empty(noMore.Asked);
        Assert.Contains(chunks, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning);

        // Without a way to ask, the chat is trimmed as it always was.
        var model = new WindowedModel();
        var trimmed = await ReadAllAsync(Orchestrator(model, questions: null).AskAsync(LongChat(), "And now?"));
        Assert.Contains(trimmed, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning);
        Assert.False(model.Documents);
    }

    // ---- a tool call written out as words ----

    private static ModelInfo ToolModel => new("test-model", 8192) { SupportsToolCalling = true };

    [Theory]
    [InlineData("Sure, I'll send it now. <tool|_call>\n<function=draft_message>\n<parameter=recipient>marcus</parameter>|</function></tool_call>")]
    [InlineData("Sure, I'll send it now.|\n<function=draft_message>{\"recipient\":\"marcus\"}</function>")]
    [InlineData("Sure, I'll send it now. [TOOL_CALLS] [{\"name\":\"draft_message\"}]")]
    public async Task AToolCallTheModelWritesOutAsWordsIsNotShownAsItsAnswer(string written)
    {
        // The pieces the words arrive in are split at |.
        var model = new SequencedModel(ToolModel, [.. written.Split('|').Select(AssistantResponseChunk.ForTextDelta)]);
        var session = ConversationSession.Start(_clock);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance);

        var chunks = await ReadAllAsync(orchestrator.AskAsync(session, "message my brother hello"));

        var said = string.Concat(chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.TextDelta).Select(chunk => chunk.Text));
        Assert.Equal("Sure, I'll send it now.", said.Trim());
        Assert.Equal("Sure, I'll send it now.", session.Conversation.Messages[^1].Text.Trim());
    }

    [Theory]
    [InlineData("Use a < b, or <|b>.", "Use a < b, or <b>.")] // Something that only begins like a marker is words.
    [InlineData("The answer ends with <tool", "The answer ends with <tool")] // Held back to the end, and then given.
    [InlineData("Plain words.", "Plain words.")]
    public async Task WordsThatOnlyLookLikeTheStartOfAToolCallAreShownWhole(string written, string expected)
    {
        var model = new SequencedModel(ToolModel, [.. written.Split('|').Select(AssistantResponseChunk.ForTextDelta)]);
        var session = ConversationSession.Start(_clock);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance);

        var chunks = await ReadAllAsync(orchestrator.AskAsync(session, "hello"));

        Assert.Equal(expected, string.Concat(chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.TextDelta).Select(chunk => chunk.Text)));
        Assert.Equal(expected, session.Conversation.Messages[^1].Text);
    }

    // ---- a question that came with a picture ----

    private sealed class RecordingRegistry(params ToolDefinition[] tools) : IToolRegistry
    {
        public List<ToolContext> Asked { get; } = [];

        public IReadOnlyList<ToolDefinition> Tools { get; } = tools;

        public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context)
        {
            Asked.Add(context);
            return Tools;
        }

        public ToolDefinition? Find(string name) => Tools.FirstOrDefault(tool => tool.Name == name);
    }

    private static readonly ToolDefinition Screenshot = new("take_screenshot", "Takes a picture of the screen.", """{"type":"object","properties":{}}""", RiskLevel.SideEffect);

    [Theory]
    [InlineData(ContextItemType.Image, true)]
    [InlineData(ContextItemType.Screenshot, true)]
    [InlineData(ContextItemType.Selection, false)]
    public async Task TheToolsAreToldWhetherTheQuestionCameWithAPicture_AndSoIsEveryCallOfTheTurn(ContextItemType type, bool picture)
    {
        var registry = new RecordingRegistry(Screenshot);
        var executor = new FakeToolExecutor("""{"done":true}""");
        var model = new SequencedModel(ToolModel, [AssistantResponseChunk.ForToolCall(new ToolCall("c1", "take_screenshot", "{}"))], [AssistantResponseChunk.ForTextDelta("It says hello.")]);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: registry, toolExecutor: executor);
        var item = new ContextItem(Guid.NewGuid(), type, "pasted") { Text = "hello" };

        await ReadAllAsync(orchestrator.AskAsync(ConversationSession.Start(_clock), "what does this say?", [item]));

        Assert.NotEmpty(registry.Asked);
        Assert.All(registry.Asked, context => Assert.Equal(picture, context.HasPicture));
        Assert.Equal(picture, Assert.Single(executor.Calls).Context.HasPicture);
    }

    // ---- handles ----

    [Theory]
    [InlineData("@marcus:beeper.com", true, "marcus", "Beeper", "Marcus")]
    [InlineData("sami.k@example.com", true, "sami.k", "", "Sami K")]
    [InlineData("@lena_m:matrix.org", true, "lena_m", "Matrix", "Lena M")]
    [InlineData("Marcus", false, "", "", "")]
    [InlineData("my brother", false, "", "", "")]
    [InlineData("@@", false, "", "", "")]
    public void AHandleIsReadForTheNameInItAndTheServiceItIsFor(string text, bool isHandle, string local, string service, string name)
    {
        Assert.Equal(isHandle, PersonHandles.IsHandle(text));
        Assert.Equal(local, PersonHandles.LocalPart(text));
        Assert.Equal(service, PersonHandles.Service(text));
        Assert.Equal(name, PersonHandles.NameOf(text));
    }
}
