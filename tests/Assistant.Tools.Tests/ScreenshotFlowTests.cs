using System.Runtime.CompilerServices;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Ocr;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Assistant.Tools.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// A whole answer in which the model takes a screenshot and then reads it (PROJECT_SPEC §4.6, §4.8): the picture joins the conversation in
/// the middle of the turn, the tool that reads a screenshot is offered from then on, and what it reads goes back to the model, with the
/// real orchestrator, executor, registry, context service and screen-text service.
/// </summary>
public sealed class ScreenshotFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A screenshot taker that does what the app's does to the context service: the picture is the conversation's, kept for its questions.
    private sealed class AttachingScreenshots(IContextService contexts) : IScreenshotTaker
    {
        public int Taken { get; private set; }

        public Task<ScreenshotOutcome> TakeAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            Taken++;
            contexts.Add(
                conversationId,
                new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot")
                {
                    ImageData = new byte[] { 1, 2, 3, 4 },
                    Source = ContextSource.UserSelected,
                    Retained = true,
                },
                "test");
            return Task.FromResult(new ScreenshotOutcome(ScreenshotStatus.Taken, 1920, 1080));
        }
    }

    private sealed class DialogOcr : IOcrEngine
    {
        public bool IsAvailable => true;

        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult([new OcrLine("Error 404: file not found", new OcrBox(40, 20, 300, 20), [])], 400, 200, "en-US"));
    }

    private sealed class NoImages : IImagePreprocessor
    {
        public Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedModel(params IReadOnlyList<AssistantResponseChunk>[] rounds) : IModelService
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var chunk in _next < rounds.Length ? rounds[_next++] : [])
            {
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    private static AssistantResponseChunk Calls(string tool) => AssistantResponseChunk.ForToolCall(new ToolCall("c", tool, "{}"));

    private sealed class Setup
    {
        public required AssistantOrchestrator Orchestrator { get; init; }
        public required ScriptedModel Model { get; init; }
        public required AttachingScreenshots Screenshots { get; init; }
        public required IContextService Contexts { get; init; }
        public required ConversationSession Session { get; init; }
        public required FakeConfirmation Confirmation { get; init; }
    }

    private static Setup Create(bool confirm, params IReadOnlyList<AssistantResponseChunk>[] rounds)
    {
        var clock = new Clock(Start);
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()), clock);
        var screens = new ScreenTextService(contexts, new DialogOcr());
        var screenshots = new AttachingScreenshots(contexts);
        var confirmation = new FakeConfirmation(confirm);
        ITool[] tools = [new TakeScreenshotTool(screenshots, screens), new ReadScreenTextTool(screens)];
        var model = new ScriptedModel(rounds);
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new NoImages(), clock, NullLogger<AssistantOrchestrator>.Instance,
            contexts, new ToolRegistry(tools), new ToolExecutor(tools, confirmation, new FakePermissions()), screens);
        return new Setup
        {
            Orchestrator = orchestrator, Model = model, Screenshots = screenshots, Contexts = contexts, Confirmation = confirmation,
            Session = ConversationSession.Start(clock),
        };
    }

    private static async Task<List<AssistantResponseChunk>> RunAsync(Setup setup, string prompt)
    {
        var chunks = new List<AssistantResponseChunk>();
        await foreach (var chunk in setup.Orchestrator.AskAsync(setup.Session, prompt))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task TheModelTakesAScreenshot_ThenIsOfferedTheToolThatReadsIt_AndReadsIt()
    {
        var setup = Create(
            confirm: true,
            [Calls("take_screenshot")],
            [Calls("read_screen_text")],
            [AssistantResponseChunk.ForTextDelta("It says error 404.")]);

        var chunks = await RunAsync(setup, "what does my screen say?");

        Assert.Equal(1, setup.Screenshots.Taken);
        Assert.Equal(1, setup.Confirmation.Asked);
        Assert.Equal(3, setup.Model.Requests.Count);

        // Before the picture there was nothing to read; from the round after it was taken the reading tool is offered.
        Assert.Equal(["take_screenshot"], setup.Model.Requests[0].Tools.Select(tool => tool.Name));
        Assert.Equal(["take_screenshot", "read_screen_text"], setup.Model.Requests[1].Tools.Select(tool => tool.Name));

        // What was read went back to the model.
        var results = chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!).ToList();
        Assert.All(results, result => Assert.Equal(ToolResultStatus.Succeeded, result.Status));
        Assert.Contains("\"taken\":true", results[0].OutputJson, StringComparison.Ordinal);
        Assert.Contains("read_screen_text", results[0].OutputJson, StringComparison.Ordinal);
        Assert.Contains("Error 404: file not found", setup.Model.Requests[2].Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal("It says error 404.", setup.Session.Conversation.Messages[^1].Text);

        // The picture stays the conversation's, for what the user asks next.
        var kept = Assert.Single(setup.Contexts.PendingItems(setup.Session.Conversation.Id));
        Assert.Equal(ContextItemType.Screenshot, kept.Type);
        Assert.True(kept.Retained);
    }

    [Fact]
    public async Task WhenTheUserDoesNotAllowIt_NoScreenshotIsTaken_AndTheModelIsToldSo()
    {
        var setup = Create(
            confirm: false,
            [Calls("take_screenshot")],
            [AssistantResponseChunk.ForTextDelta("I was not allowed to look.")]);

        var chunks = await RunAsync(setup, "what does my screen say?");

        Assert.Equal(0, setup.Screenshots.Taken);
        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(setup.Contexts.PendingItems(setup.Session.Conversation.Id));

        // With nothing taken, the reading tool was never offered.
        Assert.All(setup.Model.Requests, request => Assert.Equal(["take_screenshot"], request.Tools.Select(tool => tool.Name)));
    }
}
