using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>The context service in a chat turn: what it hands the orchestrator, what it is told afterwards, and what the user is warned of.</summary>
public sealed class ContextPipelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(Start);
    private readonly ScriptedModel _model = new();
    private readonly ContextService _contexts;

    public ContextPipelineTests()
    {
        _contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()), _clock);
    }

    [Fact]
    public async Task ContextThatFits_IsSentInRankOrder_AndTheConversationCarriesItAfterwards()
    {
        var session = ConversationSession.Start(_clock);
        var id = session.Conversation.Id;
        var selection = Selection("Notes", "selected words");
        var file = File("Doc.txt", "file words");
        _contexts.Add(id, selection, "hotkey");
        _contexts.Add(id, file, "composer");
        _model.Write("Done.");
        _model.End();

        var chunks = await ReadAllAsync(Orchestrator().AskAsync(session, "Explain", _contexts.PendingItems(id)));

        Assert.DoesNotContain(chunks, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning);
        Assert.Equal([file.Id, selection.Id], session.Conversation.Messages[0].ContextItems.Select(item => item.Id));
        Assert.Empty(_contexts.PendingItems(id));
        Assert.Equal([file.Id, selection.Id], _contexts.GetContext(id).Earlier.Select(entry => entry.Item.Id));
    }

    [Fact]
    public async Task WhenContextDoesNotFit_TheUserIsWarned_BeforeTheAnswer_AndTheServiceRemembersWhat()
    {
        var session = ConversationSession.Start(_clock);
        var id = session.Conversation.Id;
        var small = File("Small.txt", Tokens(50));
        var big = Retrieved("Everything", Tokens(5000));
        _contexts.Add(id, small, "composer");
        _contexts.Add(id, big, "search");
        _model.Write("Done.");
        _model.End();
        var settings = new AppSettings { ContextLimits = new ContextLimitSettings { HeavyContextTokens = 1000, ReservedOutputTokens = 200 } };

        var chunks = await ReadAllAsync(Orchestrator(settings).AskAsync(session, "Explain", _contexts.PendingItems(id)));

        Assert.Equal(AssistantResponseChunkType.ContextWarning, chunks[0].Type);
        Assert.Contains("Everything", chunks[0].Text, StringComparison.Ordinal);
        Assert.Equal(AssistantResponseChunkType.TextDelta, chunks[^1].Type);
        var context = _contexts.GetContext(id);
        Assert.Equal(1, context.LeftOutCount + context.ShortenedCount);
        Assert.Equal(ContextFate.Whole, context.Earlier.Single(entry => entry.Item.Id == small.Id).LastFit);

        // The conversation itself keeps everything: only what the model was sent was cut.
        Assert.Equal(5000, session.Conversation.Messages[0].ContextItems.Single(item => item.Id == big.Id).Text!.Split(' ').Length);
    }

    [Fact]
    public async Task AQuestionThatNeverReachedTheModel_LeavesItsContextWaiting()
    {
        var session = ConversationSession.Start(_clock);
        var id = session.Conversation.Id;
        _contexts.Add(id, File("F", "text"), "composer");
        var noModel = new ScriptedModel { Active = null };

        await Assert.ThrowsAsync<ModelNotSetUpException>(
            async () => await ReadAllAsync(Orchestrator(model: noModel).AskAsync(session, "Explain", _contexts.PendingItems(id))));

        Assert.Single(_contexts.PendingItems(id));
    }

    private AssistantOrchestrator Orchestrator(AppSettings? settings = null, ScriptedModel? model = null) =>
        new(
            model ?? _model,
            new FixedSettings(settings),
            new PromptBuilder(_contexts),
            new FakeImagePreprocessor(),
            _clock,
            NullLogger<AssistantOrchestrator>.Instance,
            _contexts);

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }
}
