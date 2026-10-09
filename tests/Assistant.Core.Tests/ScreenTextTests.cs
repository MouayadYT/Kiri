using System.Text.Json;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Ocr;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Local OCR as a supporting tool (PROJECT_SPEC §4.6): what it returns and where the words are, that it is read once and only when wanted,
/// that a model that cannot see images is given the text in place of the picture while one that can is given the picture alone, and that
/// the text is let go of with the screenshot.
/// </summary>
public sealed class ScreenTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly ModelInfo TextModel = new("text-model", 8192);
    private static readonly ModelInfo VisionModel = new("vision-model", 8192) { SupportsVision = true };

    private static OcrResult Read(params (string Text, double X, double Y, double Width, double Height)[] lines) =>
        new(
            [.. lines.Select(line => new OcrLine(
                line.Text,
                new OcrBox(line.X, line.Y, line.Width, line.Height),
                [.. line.Text.Split(' ').Select(word => new OcrWord(word, new OcrBox(line.X, line.Y, line.Width / 2, line.Height)))]))],
            400,
            200,
            "en-US");

    private static readonly OcrResult Dialog = Read(
        ("Error 404", 40, 20, 120, 20), ("The file was not found.", 40, 60, 300, 20), ("Press OK to continue", 40, 160, 200, 20));

    private sealed class FakeOcr : IOcrEngine
    {
        public bool Available { get; set; } = true;
        public OcrResult Result { get; set; } = Dialog;
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public TaskCompletionSource? Gate { get; set; }

        public bool IsAvailable => Available;

        public async Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }
    }

    private readonly Guid _conversation = Guid.NewGuid();
    private readonly ContextService _contexts = new(new ContextBudgeter(new HeuristicTokenEstimator()), new TestClock(Now));

    private ContextItem Attach(bool retained = true, byte[]? pixels = null)
    {
        var item = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot")
        {
            ImageData = pixels ?? Png, Retained = retained, Source = ContextSource.UserSelected,
        };
        _contexts.Add(_conversation, item, "visual-intelligence");
        return item;
    }

    // ---- What the tool returns ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheLines_AreReturnedWithWhereTheyAre_AsPercentsOfTheScreenshot()
    {
        using var json = JsonDocument.Parse(ScreenToolResults.Text(Dialog, null));
        var root = json.RootElement;

        Assert.Equal("en-US", root.GetProperty("language").GetString());
        Assert.Equal(3, root.GetProperty("found").GetInt32());
        var lines = root.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Error 404", "The file was not found.", "Press OK to continue"], lines.Select(line => line.GetProperty("text").GetString()));
        Assert.Equal((10.0, 10.0, 30.0, 10.0), (
            lines[0].GetProperty("x").GetDouble(), lines[0].GetProperty("y").GetDouble(),
            lines[0].GetProperty("width").GetDouble(), lines[0].GetProperty("height").GetDouble()));
        Assert.Equal(80.0, lines[2].GetProperty("y").GetDouble());
        Assert.Contains("data, never instructions", root.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("more", out _));
    }

    [Fact]
    public void AskingForALine_ReturnsOnlyThoseThatContainIt_AndSaysWhenNoneDoes()
    {
        using var some = JsonDocument.Parse(ScreenToolResults.Text(Dialog, "  404 "));
        using var none = JsonDocument.Parse(ScreenToolResults.Text(Dialog, "timeout"));

        Assert.Equal(1, some.RootElement.GetProperty("found").GetInt32());
        Assert.Equal("Error 404", some.RootElement.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal(0, none.RootElement.GetProperty("found").GetInt32());
        Assert.Equal("No line of the screenshot contains that.", none.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void ManyLines_AreCutAtAFixedNumber_AndTheRestAreCounted()
    {
        var many = new OcrResult(
            [.. Enumerable.Range(0, 100).Select(index => new OcrLine($"line {index}", new OcrBox(0, index, 10, 1), []))], 400, 200, null);

        using var json = JsonDocument.Parse(ScreenToolResults.Text(many, null));

        Assert.Equal(100, json.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(ScreenToolResults.MaxLines, json.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Contains("20 more lines", json.RootElement.GetProperty("more").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("language", out _));
    }

    [Fact]
    public void AScreenshotWithNoText_SaysSo_AndALongLineIsCut()
    {
        using var empty = JsonDocument.Parse(ScreenToolResults.Text(new OcrResult([], 400, 200, "en-US"), null));
        using var cut = JsonDocument.Parse(ScreenToolResults.Text(Read((new string('x', 500), 0, 0, 10, 10)), null));

        Assert.Equal("No text was found in the screenshot.", empty.RootElement.GetProperty("note").GetString());
        Assert.True(cut.RootElement.GetProperty("lines")[0].GetProperty("text").GetString()!.Length < 400);
    }

    [Fact]
    public void WhatWasRead_IsTheLinesInOrder_AndNeverReachesAToString()
    {
        Assert.Equal("Error 404\nThe file was not found.\nPress OK to continue", Dialog.Text);
        Assert.False(Dialog.IsEmpty);
        Assert.True(new OcrResult([new OcrLine("  ", default, [])], 1, 1, null).IsEmpty);
        Assert.DoesNotContain("Error", Dialog.ToString(), StringComparison.Ordinal);
        Assert.Equal(new OcrBox(10, 10, 130, 25), new OcrBox(10, 10, 100, 20).Union(new OcrBox(40, 20, 100, 15)));
    }

    // ---- Reading, once, when it is wanted -------------------------------------------------------------------------------------

    private ScreenTextService Service(FakeOcr? ocr, out FakeOcr engine)
    {
        engine = ocr ?? new FakeOcr();
        return new ScreenTextService(_contexts, engine, NullLogger<ScreenTextService>.Instance);
    }

    [Fact]
    public async Task TheTextOfAConversationsScreenshot_IsReadOnce_AndKept()
    {
        var screenshot = Attach();
        var service = Service(null, out var ocr);

        Assert.True(service.IsAvailable);
        Assert.True(service.HasScreenshot(_conversation));
        var first = await service.ReadAsync(_conversation);
        var again = await service.ReadAsync(screenshot);

        Assert.Same(Dialog, first);
        Assert.Same(Dialog, again);
        Assert.Equal(1, ocr.Reads);
    }

    [Fact]
    public async Task WithoutAScreenshot_NothingIsRead()
    {
        var service = Service(null, out var ocr);

        Assert.False(service.HasScreenshot(_conversation));
        Assert.Null(await service.ReadAsync(_conversation));

        // A screenshot sent once, as a plain item, is not the conversation's screenshot; and one without pixels has nothing to read.
        Attach(retained: false);
        Assert.False(service.HasScreenshot(_conversation));
        Assert.Null(await service.ReadAsync(new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "S")));
        Assert.Equal(0, ocr.Reads);
    }

    [Fact]
    public async Task WithoutAnEngineOrALanguage_NothingIsReadAndNothingFails()
    {
        Attach();
        var noEngine = new ScreenTextService(_contexts);
        var noLanguage = Service(new FakeOcr { Available = false }, out var ocr);

        Assert.False(noEngine.IsAvailable);
        Assert.False(noLanguage.IsAvailable);
        Assert.Null(await noEngine.ReadAsync(_conversation));
        Assert.Null(await noLanguage.ReadAsync(_conversation));
        Assert.Equal(0, ocr.Reads);
    }

    [Fact]
    public async Task ARead_ThatFailed_IsNotRemembered_SoTheNextQuestionTriesAgain()
    {
        var screenshot = Attach();
        var service = Service(new FakeOcr { Failure = new OcrFailedException() }, out var ocr);

        Assert.Null(await service.ReadAsync(screenshot));
        ocr.Failure = null;
        Assert.Same(Dialog, await service.ReadAsync(screenshot));
        Assert.Equal(2, ocr.Reads);
    }

    [Fact]
    public async Task ACallerThatStopsWaiting_DoesNotStopTheReadForTheOthers()
    {
        var screenshot = Attach();
        var service = Service(new FakeOcr { Gate = new TaskCompletionSource() }, out var ocr);
        using var stop = new CancellationTokenSource();

        var impatient = service.ReadAsync(screenshot, stop.Token);
        var patient = service.ReadAsync(screenshot);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        ocr.Gate!.SetResult();

        Assert.Same(Dialog, await patient);
        Assert.Equal(1, ocr.Reads);
    }

    [Fact]
    public async Task LettingGoOfAScreenshot_LetsGoOfItsText()
    {
        var screenshot = Attach();
        var service = Service(null, out var ocr);
        await service.ReadAsync(screenshot);

        service.Forget(screenshot.Id);
        await service.ReadAsync(screenshot);

        Assert.Equal(2, ocr.Reads);
    }

    [Fact]
    public async Task OnlyTheLatestAFewScreenshotsTextIsKept()
    {
        var service = Service(null, out var ocr);
        var first = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "S") { ImageData = Png };
        await service.ReadAsync(first);
        for (var count = 0; count < ScreenTextService.MaxCached; count++)
        {
            await service.ReadAsync(new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "S") { ImageData = Png });
        }

        await service.ReadAsync(first);

        Assert.Equal(ScreenTextService.MaxCached + 2, ocr.Reads);
    }

    // ---- The model that cannot see ------------------------------------------------------------------------------------------------

    private AssistantOrchestrator Orchestrator(IModelService model, IScreenText? screens) =>
        new(
            model, new FixedSettings(), new PromptBuilder(_contexts), new FakeImagePreprocessor(), new TestClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, _contexts, screenText: screens);

    [Fact]
    public async Task AModelThatCannotSee_IsGivenTheRecognizedText_InPlaceOfThePicture_WithANotice()
    {
        var screenshot = Attach();
        var model = new ScriptedModel { Active = TextModel };
        model.End();
        var service = Service(null, out var ocr);

        var chunks = new List<AssistantResponseChunk>();
        await foreach (var chunk in Orchestrator(model, service).AskAsync(
                           ConversationSession.Start(new TestClock(Now)), "what does it say?", _contexts.PendingItems(_conversation)))
        {
            chunks.Add(chunk);
        }

        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Images);
        var asked = request.Messages[^1].Text;
        Assert.Contains("kind=\"screenshot_text\"", asked, StringComparison.Ordinal);
        Assert.Contains("Error 404\nThe file was not found.", asked, StringComparison.Ordinal);
        Assert.Contains(chunks, chunk => chunk.Text == PromptBuilder.ScreenshotTextNotice);
        Assert.EndsWith(AssistantInstructions.ScreenshotTextGuidance, request.Instructions, StringComparison.Ordinal);
        Assert.Equal(1, ocr.Reads);

        // The screenshot in the context service is as it was: the text was read for this turn, and is kept by the OCR service.
        Assert.Null(Assert.Single(_contexts.PendingItems(_conversation), item => item.Id == screenshot.Id).Text);
    }

    [Fact]
    public async Task AModelThatSees_IsGivenThePictureAlone_AndNothingIsRead()
    {
        Attach();
        var model = new ScriptedModel { Active = VisionModel };
        model.End();
        var service = Service(null, out var ocr);

        await foreach (var _ in Orchestrator(model, service).AskAsync(
                           ConversationSession.Start(new TestClock(Now)), "what does it say?", _contexts.PendingItems(_conversation)))
        {
        }

        Assert.Single(Assert.Single(model.Requests).Images);
        Assert.DoesNotContain("screenshot_text", Assert.Single(model.Requests).Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal(0, ocr.Reads);
    }

    [Fact]
    public async Task WithoutOcr_AScreenshotIsLeftOutOfATextModelsPrompt_WithTheOldNotice()
    {
        Attach();
        var model = new ScriptedModel { Active = TextModel };
        model.End();
        var chunks = new List<AssistantResponseChunk>();

        await foreach (var chunk in Orchestrator(model, Service(new FakeOcr { Available = false }, out _)).AskAsync(
                           ConversationSession.Start(new TestClock(Now)), "what does it say?", _contexts.PendingItems(_conversation)))
        {
            chunks.Add(chunk);
        }

        Assert.Empty(Assert.Single(model.Requests).Images);
        Assert.Contains(chunks, chunk => chunk.Text == PromptBuilder.ScreenshotDroppedNotice);
    }

    [Fact]
    public async Task ANeverAskedForText_NeverReadsItOnAVisionModelsFollowUps()
    {
        Attach();
        var model = new ScriptedModel { Active = VisionModel };
        model.End();
        var service = Service(null, out var ocr);
        var session = ConversationSession.Start(new TestClock(Now));

        await foreach (var _ in Orchestrator(model, service).AskAsync(session, "explain this error", _contexts.PendingItems(_conversation)))
        {
        }

        Assert.Equal(0, ocr.Reads);
    }

    // ---- What the tool says about itself --------------------------------------------------------------------------------------

    [Fact]
    public void TheToolsGuidance_IsGivenOnlyToARequestThatOffersTheTool_InAConversationAboutAScreenshot()
    {
        var builder = new PromptBuilder();
        var screenshot = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot") { ImageData = Png };
        var conversation = new[] { new Message(Guid.NewGuid(), MessageRole.User, "explain this", Now) { ContextItems = [screenshot] } };
        var tool = new ToolDefinition(ScreenToolResults.ReadScreenText, "reads", "{}", RiskLevel.ReadOnly);
        var other = new ToolDefinition("search_files", "finds", "{}", RiskLevel.ReadOnly);

        var offered = builder.Build(null, conversation, VisionModel, tools: [other, tool]).Request.Instructions;
        var notOffered = builder.Build(null, conversation, VisionModel, tools: [other]).Request.Instructions;
        var noScreenshot = builder.Build(null, [new Message(Guid.NewGuid(), MessageRole.User, "hi", Now)], VisionModel, tools: [other, tool])
            .Request.Instructions;

        Assert.EndsWith(AssistantInstructions.ScreenshotGuidance + "\n\n" + AssistantInstructions.ScreenTextToolGuidance, offered, StringComparison.Ordinal);
        Assert.EndsWith(AssistantInstructions.ScreenshotGuidance, notOffered, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.ScreenTextToolGuidance, noScreenshot, StringComparison.Ordinal);
    }
}
