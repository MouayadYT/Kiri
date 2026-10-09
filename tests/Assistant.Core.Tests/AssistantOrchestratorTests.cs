using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>A chat turn: the prompt, the streamed answer, and the in-memory conversation it updates.</summary>
public sealed class AssistantOrchestratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Start);
    private readonly ScriptedModel _model = new();

    [Fact]
    public async Task ATurn_BuildsTheRequest_StreamsTheAnswer_AndRecordsBothMessages()
    {
        var session = ConversationSession.Start(_clock);
        var selection = new ContextItem(Guid.NewGuid(), ContextItemType.Selection, "Notes") { Text = "Some selected text" };
        _model.Write("Because ");
        _model.Write("light scatters.");
        _model.End();
        _clock.Advance(TimeSpan.FromSeconds(1));

        var chunks = await ReadAllAsync(
            Orchestrator().AskAsync(session, "Explain this", [selection], "Be brief."));

        Assert.Equal(["Because ", "light scatters."], chunks.Select(chunk => chunk.Text));

        var request = Assert.Single(_model.Requests);
        Assert.StartsWith("Be brief.", request.Instructions, StringComparison.Ordinal);
        Assert.EndsWith(AssistantInstructions.UntrustedContextGuidance, request.Instructions, StringComparison.Ordinal);
        var asked = Assert.Single(request.Messages);
        Assert.Contains("Some selected text", asked.Text, StringComparison.Ordinal);
        Assert.EndsWith("Explain this", asked.Text, StringComparison.Ordinal);

        // The conversation holds the user's message as typed, with its context, and the whole answer.
        var conversation = session.Conversation;
        Assert.Equal(Start, conversation.CreatedAt);
        Assert.Equal(Start.AddSeconds(1), conversation.UpdatedAt);
        Assert.Equal(
            [(MessageRole.User, "Explain this"), (MessageRole.Assistant, "Because light scatters.")],
            conversation.Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal([selection], conversation.Messages[0].ContextItems);
        Assert.Equal(Start.AddSeconds(1), conversation.Messages[0].CreatedAt);
        Assert.Equal(Start.AddSeconds(1), conversation.Messages[1].CreatedAt);
    }

    [Fact]
    public async Task TheConversation_HoldsWhatTheModelHasSaid_WhileTheAnswerStreams()
    {
        var session = ConversationSession.Start(_clock);
        await using var chunks = Orchestrator().AskAsync(session, "Tell a story").GetAsyncEnumerator();

        _model.Write("Once");
        Assert.True(await chunks.MoveNextAsync());
        Assert.Equal(
            [(MessageRole.User, "Tell a story"), (MessageRole.Assistant, "Once")],
            session.Conversation.Messages.Select(message => (message.Role, message.Text)));
        var firstAnswer = session.Conversation.Messages[1];

        _clock.Advance(TimeSpan.FromSeconds(2));
        _model.Write(" upon a time");
        Assert.True(await chunks.MoveNextAsync());

        var conversation = session.Conversation;
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal("Once upon a time", conversation.Messages[1].Text);
        Assert.Equal(firstAnswer.Id, conversation.Messages[1].Id);
        Assert.Equal(firstAnswer.CreatedAt, conversation.Messages[1].CreatedAt);
        Assert.Equal(Start.AddSeconds(2), conversation.UpdatedAt);
        Assert.Equal("Once", firstAnswer.Text);

        _model.End();
        Assert.False(await chunks.MoveNextAsync());
    }

    [Fact]
    public async Task AFollowUp_CarriesTheEarlierTurns_OldestFirst()
    {
        var session = ConversationSession.Start(_clock);
        var orchestrator = Orchestrator();
        _model.Write("Paris.");
        _model.End();
        await ReadAllAsync(orchestrator.AskAsync(session, "Capital of France?"));

        var second = new ScriptedModel();
        second.Write("About two million.");
        second.End();
        await ReadAllAsync(Orchestrator(second).AskAsync(session, "How many people live there?"));

        Assert.Equal(
            [
                (MessageRole.User, "Capital of France?"),
                (MessageRole.Assistant, "Paris."),
                (MessageRole.User, "How many people live there?"),
            ],
            Assert.Single(second.Requests).Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(4, session.Conversation.Messages.Count);
    }

    [Fact]
    public async Task StoppingTheAnswer_KeepsWhatItSaid_AndTheNextQuestionCarriesIt()
    {
        var session = ConversationSession.Start(_clock);
        using var stop = new CancellationTokenSource();
        var chunks = new List<AssistantResponseChunk>();
        _model.Write("A long ");
        _model.Write("answer that");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in Orchestrator().AskAsync(session, "Write an essay", cancellationToken: stop.Token))
            {
                chunks.Add(chunk);
                if (chunks.Count == 2)
                {
                    await stop.CancelAsync();
                }
            }
        });

        Assert.Equal(
            [(MessageRole.User, "Write an essay"), (MessageRole.Assistant, "A long answer that")],
            session.Conversation.Messages.Select(message => (message.Role, message.Text)));

        // The session is free again, and the next request has the stopped answer as it was.
        var next = new ScriptedModel();
        next.End();
        await ReadAllAsync(Orchestrator(next).AskAsync(session, "Go on"));
        Assert.Equal(
            ["Write an essay", "A long answer that", "Go on"],
            Assert.Single(next.Requests).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AFailureMidAnswer_KeepsWhatWasSaid_ReleasesTheSession_AndIsPassedOn()
    {
        var session = ConversationSession.Start(_clock);
        _model.Write("It is");
        _model.End(new ModelHostException(ModelHostErrorCode.GenerationFailed));

        var failure = await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(Orchestrator().AskAsync(session, "Summarize this book")));

        Assert.Equal(ModelHostErrorCode.GenerationFailed, failure.Code);
        Assert.Equal(
            [(MessageRole.User, "Summarize this book"), (MessageRole.Assistant, "It is")],
            session.Conversation.Messages.Select(message => (message.Role, message.Text)));
        var next = new ScriptedModel();
        next.End();
        await ReadAllAsync(Orchestrator(next).AskAsync(session, "Again"));
    }

    [Fact]
    public async Task AnAnswerThatFailsBeforeItsFirstWords_LeavesTheQuestion_ThenTheNextTurnJoinsThem()
    {
        var session = ConversationSession.Start(_clock);
        _model.End(new ModelHostException(ModelHostErrorCode.ContextExceeded));
        await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(Orchestrator().AskAsync(session, "Too much")));

        var message = Assert.Single(session.Conversation.Messages);
        Assert.Equal((MessageRole.User, "Too much"), (message.Role, message.Text));

        // Two questions in a row would trip chat templates that need the roles to alternate.
        var next = new ScriptedModel();
        next.End();
        await ReadAllAsync(Orchestrator(next).AskAsync(session, "Less"));
        var joined = Assert.Single(Assert.Single(next.Requests).Messages);
        Assert.Equal("Too much\n\nLess", joined.Text);
    }

    [Fact]
    public async Task WithoutAModel_TheTurnDoesNotStart_AndTheConversationIsUntouched()
    {
        var session = ConversationSession.Start(_clock);
        var before = session.Conversation;
        var model = new ScriptedModel { Active = null };

        await Assert.ThrowsAsync<ModelNotSetUpException>(
            () => ReadAllAsync(Orchestrator(model).AskAsync(session, "Hello")));

        Assert.Same(before, session.Conversation);
        Assert.Empty(model.Requests);

        // A model that is there afterwards is asked in the same session.
        _model.End();
        await ReadAllAsync(Orchestrator().AskAsync(session, "Hello again"));
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task NoticesAboutThePromptComeFirst_AndNoNoticeBecomesPartOfTheAnswer()
    {
        var session = ConversationSession.Start(_clock);
        var emptyFile = new ContextItem(Guid.NewGuid(), ContextItemType.File, "Empty.txt");
        _model.Write("Done.");
        _model.Write(AssistantResponseChunk.ForNotice(LocalModelService.OutputLimitNotice));
        _model.End();

        var chunks = await ReadAllAsync(Orchestrator().AskAsync(session, "Read it", [emptyFile]));

        Assert.Equal(
            [
                (AssistantResponseChunkType.Notice, PromptBuilder.EmptyContextNotice),
                (AssistantResponseChunkType.TextDelta, "Done."),
                (AssistantResponseChunkType.Notice, LocalModelService.OutputLimitNotice),
            ],
            chunks.Select(chunk => (chunk.Type, chunk.Text!)));

        // What the model is later shown of its own answer is what it said, not what the app told the user.
        Assert.Equal("Done.", session.Conversation.Messages[1].Text);
    }

    [Fact]
    public async Task ToolCallsPassThrough_ButAreNotRecordedOrRun()
    {
        var session = ConversationSession.Start(_clock);
        var call = AssistantResponseChunk.ForToolCall(new ToolCall("c1", "search_files", "{}"));
        _model.Write(call);
        _model.End();

        var chunks = await ReadAllAsync(Orchestrator().AskAsync(session, "Find it"));

        Assert.Equal([call], chunks);
        Assert.Equal(MessageRole.User, Assert.Single(session.Conversation.Messages).Role);
    }

    [Fact]
    public async Task ASessionAnswersOneMessageAtATime_ThenAgain()
    {
        var session = ConversationSession.Start(_clock);
        var orchestrator = Orchestrator();
        await using var running = orchestrator.AskAsync(session, "First").GetAsyncEnumerator();
        _model.Write("Working");
        await running.MoveNextAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(orchestrator.AskAsync(session, "Second")));
        Assert.Equal(2, session.Conversation.Messages.Count);

        _model.End();
        Assert.False(await running.MoveNextAsync());
        var next = new ScriptedModel();
        next.Write("Reply");
        next.End();
        await ReadAllAsync(Orchestrator(next).AskAsync(session, "Second"));
        Assert.Equal(4, session.Conversation.Messages.Count);
    }

    [Fact]
    public async Task LeavingTheStreamEarly_ReleasesTheSession_AndKeepsWhatWasRead()
    {
        var session = ConversationSession.Start(_clock);
        _model.Write("Hello");
        _model.Write(" there");

        await foreach (var _ in Orchestrator().AskAsync(session, "Hi"))
        {
            break;
        }

        Assert.Equal("Hello", session.Conversation.Messages[1].Text);
        var next = new ScriptedModel();
        next.End();
        await ReadAllAsync(Orchestrator(next).AskAsync(session, "Again"));
    }

    [Fact]
    public async Task TheCallersContextList_IsCopied_SoLaterChangesDoNotReachTheConversation()
    {
        var session = ConversationSession.Start(_clock);
        var context = new List<ContextItem> { new(Guid.NewGuid(), ContextItemType.Selection, "Notes") { Text = "Text" } };
        _model.End();

        await ReadAllAsync(Orchestrator().AskAsync(session, "Look", context));
        context.Clear();

        Assert.Single(session.Conversation.Messages[0].ContextItems);
    }

    [Fact]
    public void AskingWithoutAPrompt_IsRefusedAtOnce()
    {
        var orchestrator = Orchestrator();
        var session = ConversationSession.Start(_clock);

        Assert.Throws<ArgumentException>(() => orchestrator.AskAsync(session, "  "));
        Assert.Throws<ArgumentNullException>(() => orchestrator.AskAsync(null!, "Hi"));
        Assert.Empty(session.Conversation.Messages);
    }

    [Fact]
    public async Task ATurn_LogsNoPrivateContent_ButDoesLogItsOutcome()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var session = ConversationSession.Start(_clock);
        var context = new ContextItem(Guid.NewGuid(), ContextItemType.File, "PRIVATE-NAME.docx")
        {
            FilePath = "C:\\PRIVATE-PATH\\PRIVATE-NAME.docx",
            Text = "PRIVATE-FILE-TEXT",
        };
        _model.Write("PRIVATE-ANSWER");
        _model.End();
        var orchestrator = new AssistantOrchestrator(
            _model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), _clock, loggers.CreateLogger<AssistantOrchestrator>());

        await ReadAllAsync(orchestrator.AskAsync(session, "PRIVATE-QUESTION", [context], "PRIVATE-INSTRUCTIONS"));

        Assert.Contains("Chat turn ended: Completed", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Chat turn started", capture.AllText, StringComparison.Ordinal);
        foreach (var secret in new[]
                 {
                     "PRIVATE-QUESTION", "PRIVATE-ANSWER", "PRIVATE-FILE-TEXT", "PRIVATE-NAME", "PRIVATE-PATH",
                     "PRIVATE-INSTRUCTIONS",
                 })
        {
            Assert.DoesNotContain(secret, capture.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheAnswer_IsCappedAtTheTokensReservedForIt_WhichTheSettingsSet()
    {
        var session = ConversationSession.Start(_clock);
        var settings = new AppSettings { ContextLimits = new ContextLimitSettings { ReservedOutputTokens = 300 } };
        _model.End();

        await ReadAllAsync(Orchestrator(settings: settings).AskAsync(session, "Hello"));
        var second = new ScriptedModel();
        second.End();
        await ReadAllAsync(Orchestrator(second).AskAsync(session, "Again"));

        Assert.Equal(300, Assert.Single(_model.Requests).MaxOutputTokens);
        Assert.Equal(1024, Assert.Single(second.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task ALongConversation_IsTrimmedForTheModel_TheUserIsToldFirst_AndTheSessionKeepsEverything()
    {
        var earlier = Enumerable.Range(1, 20)
            .SelectMany(number => new[]
            {
                new Message(Guid.NewGuid(), MessageRole.User, $"Question {number} " + string.Join(' ', Enumerable.Repeat("word", 100)), Start),
                new Message(Guid.NewGuid(), MessageRole.Assistant, $"Answer {number} " + string.Join(' ', Enumerable.Repeat("word", 100)), Start),
            })
            .ToArray();
        var session = new ConversationSession(
            new Conversation(Guid.NewGuid(), string.Empty, Start, Start) { Messages = earlier });
        var settings = new AppSettings
        {
            ContextLimits = new ContextLimitSettings { NormalContextTokens = 1000, ReservedOutputTokens = 200 },
        };
        _model.Write("Sure.");
        _model.End();

        var chunks = await ReadAllAsync(Orchestrator(settings: settings).AskAsync(session, "And now?"));

        Assert.Equal(
            [(AssistantResponseChunkType.ContextWarning, ContextBudgetNotices.EarlierMessagesLeftOut), (AssistantResponseChunkType.TextDelta, "Sure.")],
            chunks.Select(chunk => (chunk.Type, chunk.Text!)));
        var sent = Assert.Single(_model.Requests).Messages;
        Assert.InRange(sent.Count, 3, 10);
        Assert.Equal("And now?", sent[^1].Text);
        Assert.Equal(MessageRole.User, sent[0].Role);
        Assert.Equal(earlier.Length + 2, session.Conversation.Messages.Count);
        Assert.Equal(earlier.Select(message => message.Text), session.Conversation.Messages.Take(earlier.Length).Select(message => message.Text));
    }

    [Fact]
    public async Task TheLimits_AreReadFromTheSettingsAtEachTurn()
    {
        var session = ConversationSession.Start(_clock);
        var settings = new FixedSettings(new AppSettings { ContextLimits = new ContextLimitSettings() });
        var attached = new ContextItem(Guid.NewGuid(), ContextItemType.File, "Big.txt")
        {
            Text = string.Join(' ', Enumerable.Repeat("word", 2000)),
        };
        var orchestrator = new AssistantOrchestrator(
            _model, settings, new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance);
        _model.Write("Done.");
        _model.End();

        // The test model's window is 4096 tokens: with the default heavy limit above it, 2000 words fit whole.
        var roomy = await ReadAllAsync(orchestrator.AskAsync(session, "Read it", [attached]));

        // The file stays in the conversation, so the next turn is heavy too, and now the limit is lower.
        settings.Current = new AppSettings { ContextLimits = new ContextLimitSettings { HeavyContextTokens = 1500 } };
        var second = new ScriptedModel();
        second.End();
        var tight = await ReadAllAsync(
            new AssistantOrchestrator(second, settings, new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance)
                .AskAsync(session, "And again"));

        Assert.DoesNotContain(roomy, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning);
        Assert.Contains(tight, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning && chunk.Text!.Contains("Big.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATrimmedTurn_LogsOnlyCounts_NeverWhatWasTrimmed()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var session = ConversationSession.Start(_clock);
        var context = new ContextItem(Guid.NewGuid(), ContextItemType.File, "PRIVATE-NAME.docx")
        {
            Text = "PRIVATE-FILE-TEXT " + string.Join(' ', Enumerable.Repeat("word", 6000)),
        };
        var settings = new AppSettings { ContextLimits = new ContextLimitSettings { HeavyContextTokens = 1500 } };
        _model.End();
        var orchestrator = new AssistantOrchestrator(
            _model, new FixedSettings(settings), new PromptBuilder(), new FakeImagePreprocessor(), _clock, loggers.CreateLogger<AssistantOrchestrator>());

        var chunks = await ReadAllAsync(orchestrator.AskAsync(session, "PRIVATE-QUESTION", [context]));

        // The user is told which file, but the log only counts.
        Assert.Contains(chunks, chunk => chunk.Type == AssistantResponseChunkType.ContextWarning && chunk.Text!.Contains("PRIVATE-NAME", StringComparison.Ordinal));
        Assert.Contains("Prompt fitted (Heavy)", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("1 context items shortened", capture.AllText, StringComparison.Ordinal);
        foreach (var secret in new[] { "PRIVATE-QUESTION", "PRIVATE-FILE-TEXT", "PRIVATE-NAME" })
        {
            Assert.DoesNotContain(secret, capture.AllText, StringComparison.Ordinal);
        }
    }

    private AssistantOrchestrator Orchestrator(IModelService? model = null, AppSettings? settings = null) =>
        new(model ?? _model, new FixedSettings(settings), new PromptBuilder(), new FakeImagePreprocessor(), _clock, NullLogger<AssistantOrchestrator>.Instance);

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
