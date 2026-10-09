using System.Runtime.CompilerServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Orchestration;
using Assistant.Tools;
using Assistant.Tools.Files;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The chat with the file tools (PROJECT_SPEC §4.8): what the model finds and reads is shown in the conversation, a conversation that
/// has files in it goes to the model instead of the rules, and the files a conversation has are what the model can read.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static ModelInfo ToolsModelInfo => new("test-model", 8192) { SupportsToolCalling = true };

    private static AssistantResponseChunk ModelCalls(string tool, string arguments) =>
        AssistantResponseChunk.ForToolCall(new ToolCall("call_0", tool, arguments));

    // The real orchestrator, offering the real file tools over the test's file search, to a model that answers each round as told.
    private static (ModelAnswerProvider Provider, ConversationFiles Known, RoundsModel Model, ScriptedFileRequests Requests) ToolsProvider(
        FileRequestResult found, params IReadOnlyList<AssistantResponseChunk>[] rounds)
    {
        var requests = new ScriptedFileRequests { Result = found };
        var known = new ConversationFiles();
        var model = new RoundsModel(ToolsModelInfo, rounds);
        var permissions = FilesAllowed();
        ITool[] tools =
        [
            new SearchFilesTool(requests, known),
            new ReadFileTextTool(known, permissions, RealDocuments(), model, new InMemorySettingsService()),
        ];
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, toolRegistry: new ToolRegistry(tools), toolExecutor: new ToolExecutor(tools, policy: permissions));
        var provider = new ModelAnswerProvider(
            orchestrator, new FixedClock(Now), permissions, new AttachedDocuments(permissions, RealDocuments(), model, new InMemorySettingsService()),
            files: known);
        return (provider, known, model, requests);
    }

    [Fact]
    public void WhatTheModelFindsIsShownAsTheListOfFiles_AndWhatItSaysFollows() => RunSta(() =>
    {
        var found = Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(@"C:\Users\me\Downloads\Milestone Three.mhtml"), FoundFile(@"C:\Users\me\Downloads\Milestone Four.mhtml"));
        var (provider, known, model, requests) = ToolsProvider(
            found,
            [ModelCalls("search_files", """{"query":"milestone guilines"}""")],
            [AssistantResponseChunk.ForTextDelta("Which one do you mean?")]);
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(conversation, "find milestone guilines", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Equal(["find milestone guilines"], requests.Found);
        var answer = Assert.Single(shown);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        var list = Assert.IsType<FileCollection>(answer.Content[0]);
        Assert.Equal(["Milestone Three.mhtml", "Milestone Four.mhtml"], list.Files.Select(file => file.Name));
        Assert.Equal("Which one do you mean?", Assert.IsType<TextContent>(answer.Content[1]).Text);

        // The conversation knows the files by id, and the model was told the tools and the ids in its second round.
        Assert.Equal(["f1", "f2"], known.Get(conversation, ["f1", "f2"]).Select(file => file.Id));
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("\"id\":\"f1\"", model.Requests[1].Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal(["search_files", "read_file_text"], model.Requests[0].Tools.Select(tool => tool.Name));
    });

    [Fact]
    public void AModelThatLooksAndThenSaysNothing_DoesNotLeaveTheUserWithASilence() => RunSta(() =>
    {
        var (provider, _, _, _) = ToolsProvider(
            Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(@"C:\Docs\Milestone Three.mhtml")),
            [ModelCalls("search_files", """{"query":"milestone"}""")],
            []);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "find milestone", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var answer = Assert.Single(shown);
        Assert.IsType<FileCollection>(answer.Content[0]);
        Assert.Equal(ModelAnswerProvider.NoWordsAfterToolsText, Assert.IsType<TextContent>(answer.Content[1]).Text);
    });

    [Fact]
    public void PicturesTheModelFindsAreShownAsTheGallery() => RunSta(() =>
    {
        var found = new FileRequestResult(FileRequestStatus.Found)
        {
            Plan = new PlannedFileSearch(new FileSearchQuery { Filename = "screenshot", Types = [SearchResultItemType.File], Extensions = [".png"], Kind = FileKind.Picture }, FileSearchPlanSource.Template),
            Items = [FoundFile(@"C:\Pictures\one.png"), FoundFile(@"C:\Pictures\two.png")],
        };
        var (provider, _, _, _) = ToolsProvider(
            found,
            [ModelCalls("search_files", """{"query":"my screenshots"}""")],
            [AssistantResponseChunk.ForTextDelta("Here they are.")]);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "show my screenshots", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Equal(["one.png", "two.png"], Assert.IsType<ImageCollection>(Assert.Single(shown).Content[0]).Images.Select(image => image.Name));
    });

    [Fact]
    public void WhatTheModelReadsOfAFileIsAnswered_AndTheUserIsToldWhenNotAllOfItWasRead() => RunSta(() =>
    {
        using var file = new TempDocument("long.md", string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"# Part {i}\n\n" + string.Join(' ', Enumerable.Repeat($"sentence{i}", 220)))));
        var (provider, known, model, _) = ToolsProvider(
            Found(new FileSearchQuery { Filename = "long" }, FoundFile(file.Path)),
            [ModelCalls("search_files", """{"query":"long"}"""), ModelCalls("read_file_text", """{"file":"f1","question":"summary"}""")],
            [AssistantResponseChunk.ForTextDelta("It has forty parts.")]);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "what is long.md about", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var answer = Assert.Single(shown);
        Assert.IsType<FileCollection>(answer.Content[0]);
        Assert.Contains("long.md", Assert.IsType<TextContent>(answer.Content[1]).Text, StringComparison.Ordinal);
        Assert.Contains("only", Assert.IsType<TextContent>(answer.Content[1]).Text, StringComparison.Ordinal);
        Assert.Equal("It has forty parts.", Assert.IsType<TextContent>(answer.Content[2]).Text);

        // The model was given the file's passages as untrusted context, with where each is.
        var last = model.Requests[^1].Messages[^1].Text;
        Assert.Contains("untrusted_context", last, StringComparison.Ordinal);
        Assert.Contains("[Passage", last, StringComparison.Ordinal);
    });

    [Fact]
    public void AFileAttachedToAQuestionIsKnownToTheConversation_SoTheModelCanReadItAgain() => RunSta(() =>
    {
        using var file = new TempDocument("plan.md", "The plan is to build a harbor.");
        var (provider, known, model, _) = ToolsProvider(
            new FileRequestResult(FileRequestStatus.NothingFound),
            [AssistantResponseChunk.ForTextDelta("A harbor.")]);
        var conversation = Guid.NewGuid();
        var question = new MessageViewModel(MessageRole.User, "what is the plan?", null, file.Attachment);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(conversation, question, shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Equal("plan.md", known.Find(conversation, "f1")!.Name);
        Assert.True(provider.HasFileContext(conversation));
        Assert.False(provider.HasFileContext(Guid.NewGuid()));
    });

    [Fact]
    public void OnceAConversationHasFiles_WhatTheUserSaysGoesToTheModel_NotToTheRules() => RunSta(() =>
    {
        var (provider, known, model, _) = ToolsProvider(
            new FileRequestResult(FileRequestStatus.NothingFound),
            [AssistantResponseChunk.ForTextDelta("The first one, then.")]);
        var rules = new ScriptedFileRequests
        {
            IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase),
            Likely = text => true,
            Result = Found(new FileSearchQuery { Filename = "first" }, FoundFile(@"C:\Docs\first.js")),
        };
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), localModel: provider, files: CreateAnswers(rules, known));
        var conversation = Guid.NewGuid();
        known.Offer(conversation, [FoundFile(@"C:\Docs\Milestone Four.docx"), FoundFile(@"C:\Docs\Milestone Three.docx")]);
        var shown = new List<MessageViewModel>();

        // Said alone, "first one" is a name the rules would look for; in this conversation it is for the model, which has the list.
        var asked = demo.StreamAnswerAsync(conversation, "first one", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Empty(rules.Found);
        Assert.Equal("The first one, then.", Assert.Single(shown).Text);
        Assert.Equal(["first one"], model.Requests.Select(request => request.Messages[^1].Text));
    });

    [Fact]
    public void TheFirstRequestToFindFilesIsAnsweredAtOnceWithNoModel_AndStartsAConversationThatHasFiles() => RunSta(() =>
    {
        var (provider, known, model, _) = ToolsProvider(new FileRequestResult(FileRequestStatus.NothingFound), [AssistantResponseChunk.ForTextDelta("Ok.")]);
        var rules = new ScriptedFileRequests
        {
            IsRequest = text => text.StartsWith("find", StringComparison.OrdinalIgnoreCase),
            Result = Found(new FileSearchQuery { Filename = "milestone" }, FoundFile(@"C:\Docs\Milestone Three.docx")),
        };
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), localModel: provider, files: CreateAnswers(rules, known));
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(conversation, "find milestone three", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.IsType<FileCollection>(Assert.Single(shown).Content.Last());
        Assert.Empty(model.Requests);
        Assert.True(provider.HasFileContext(conversation));
        Assert.Equal("Milestone Three.docx", known.Find(conversation, "f1")!.Name);
    });

    [Fact]
    public async Task TheAppOffersTheModelTheFileTools_AndRunsThemThroughTheWrappedExecutor()
    {
        using var host = AppHost.Create();
        var services = host.Services;

        var registry = services.GetRequiredService<IToolRegistry>();
        Assert.Equal(["search_files", "read_file_text", "read_screen_text"], registry.Tools.Take(3).Select(tool => tool.Name));

        // The screen's text is offered only in a conversation that has a screenshot.
        var offered = registry.ToolsFor(new ToolContext(Guid.NewGuid())).Select(tool => tool.Name).ToList();
        Assert.Equal(["search_files", "read_file_text"], offered.Take(2));
        Assert.DoesNotContain("read_screen_text", offered);
        Assert.All(
            registry.Tools.Where(tool => tool.Name is "search_files" or "read_file_text" or "read_screen_text"),
            tool => Assert.Equal(RiskLevel.ReadOnly, tool.RiskLevel));
        Assert.Same(services.GetRequiredService<IConversationFiles>(), services.GetRequiredService<IConversationFiles>());

        var unknown = await services.GetRequiredService<IToolExecutor>()
            .ExecuteAsync(new ToolCall("c", "delete_files", "{}"), new ToolContext(Guid.NewGuid()));
        Assert.Equal(ToolResultStatus.Failed, unknown.Status);
    }

    // -- A conversation opened from the saved history is told to the model again. --

    private static MessageViewModel ListedAnswer(string text, params string[] paths)
    {
        var answer = new MessageViewModel(MessageRole.Assistant, text);
        answer.Content.Add(new FileCollection(paths.Select(path => new FileItem(SearchResultItemType.File, System.IO.Path.GetFileName(path), path, clock: new FixedClock(Now)))));
        return answer;
    }

    [Fact]
    public void TheMessagesOfASavedConversationAreToldAgain_AndTheFilesItListedAreTheModelsOwnFindWithIds()
    {
        var files = new ConversationFiles();
        var conversation = Guid.NewGuid();
        var earlier = new[]
        {
            new MessageViewModel(MessageRole.User, "find milestone guidelines"),
            ListedAnswer("I found 2 files.", @"C:\Docs\Milestone Four.mhtml", @"C:\Docs\Milestone Three.mhtml"),
            new MessageViewModel(MessageRole.User, "summarize it"),
            new MessageViewModel(MessageRole.Assistant, "Which one do you mean?"),
        };

        var messages = ConversationReplay.Rebuild(earlier, conversation, files);

        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant],
            messages.Select(message => message.Role));
        Assert.Equal("find milestone guidelines", messages[0].Text);
        Assert.Equal(("search_files", "{\"query\":\"find milestone guidelines\"}"), (messages[1].ToolCalls[0].ToolName, messages[1].ToolCalls[0].ArgumentsJson));
        Assert.Contains("\"id\":\"f2\"", messages[2].Text, StringComparison.Ordinal);
        Assert.Equal(messages[1].ToolCalls[0].Id, messages[2].ToolResult!.ToolCallId);
        Assert.Equal("I found 2 files.", messages[3].Text);
        Assert.Equal("Milestone Three.mhtml", files.Find(conversation, "f2")!.Name);
    }

    [Fact]
    public void ASavedConversationOfManyMessagesIsToldOnlyByItsLatest_AndAnEmptyOneNotAtAll()
    {
        var many = Enumerable.Range(0, 40).Select(i => new MessageViewModel(i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, "message " + i)).ToArray();

        var messages = ConversationReplay.Rebuild(many, Guid.NewGuid(), null);

        Assert.Equal(24, messages.Count);
        Assert.Equal("message 39", messages[^1].Text);
        Assert.Empty(ConversationReplay.Rebuild([], Guid.NewGuid(), null));
    }

    [Fact]
    public void AnAnswerAskedInASavedConversationTakesWhatWasSaidBeforeIntoAccount() => RunSta(() =>
    {
        var (provider, known, model, _) = ToolsProvider(
            new FileRequestResult(FileRequestStatus.NothingFound), [AssistantResponseChunk.ForTextDelta("The first.")]);
        var conversation = Guid.NewGuid();
        var earlier = new[]
        {
            new MessageViewModel(MessageRole.User, "find milestone guidelines"),
            ListedAnswer("I found 2 files.", @"C:\Docs\Milestone Four.mhtml", @"C:\Docs\Milestone Three.mhtml"),
        };

        provider.Resume(conversation, earlier);
        provider.Resume(conversation, earlier);
        var asked = provider.StreamAnswerAsync(conversation, "the first one", new List<MessageViewModel>().Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.True(provider.HasFileContext(conversation));
        var sent = Assert.Single(model.Requests).Messages;
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant, MessageRole.User],
            sent.Select(message => message.Role));
        Assert.Equal("the first one", sent[^1].Text);
    });

    [Fact]
    public void BothWindowsTellTheProviderWhatTheConversationHeldBeforeTheQuestion() => RunSta(() =>
    {
        var provider = new ResumeRecorder();
        var panel = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        panel.Messages.Add(new MessageViewModel(MessageRole.User, "find it"));
        panel.Messages.Add(new MessageViewModel(MessageRole.Assistant, "Found."));

        panel.Ask("summarize it");

        Assert.Equal(["find it", "Found."], Assert.Single(provider.Resumed).Select(message => message.Text));

        var history = new HistoryViewModel(new FixedClock(Now), null, provider);
        var card = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "one"), new MessageViewModel(MessageRole.Assistant, "two")], Now);
        provider.Resumed.Clear();

        history.Send("three");

        Assert.Equal(["one", "two"], Assert.Single(provider.Resumed).Select(message => message.Text));
        history.Stop();
    });

    private sealed class ResumeRecorder : IAnswerProvider
    {
        public List<IReadOnlyList<MessageViewModel>> Resumed { get; } = [];

        public MessageViewModel? Answer(string question) => null;

        public void Resume(Guid conversationId, IReadOnlyList<MessageViewModel> earlier) => Resumed.Add(earlier);
    }

    // A model that answers each request with the next round of chunks the test gave it, and records the requests.
    internal sealed class RoundsModel(ModelInfo active, params IReadOnlyList<AssistantResponseChunk>[] rounds) : IModelService
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInfo?>(active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = _next < rounds.Length ? rounds[_next++] : [];
            foreach (var chunk in answer)
            {
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }
}
