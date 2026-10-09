using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Image context (<see cref="ContextItemType.Image"/>) on its way to a vision model: how formats are told apart, the
/// size an image is sent at, how the prompt carries it, and the orchestrator preparing it without touching the original.
/// </summary>
public sealed class ImageRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModelInfo TextModel = new("text-model", 4096);
    private static readonly ModelInfo VisionModel = new("vision-model", 4096) { SupportsVision = true };
    private static readonly byte[] Pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    // ---- Formats -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("89504E470D0A1A0A0000", ImageFormat.Png, "image/png")]
    [InlineData("FFD8FFE000104A464946", ImageFormat.Jpeg, "image/jpeg")]
    [InlineData("474946383961", ImageFormat.Gif, "image/gif")]
    [InlineData("424D360000000000000036000000", ImageFormat.Bmp, "image/bmp")]
    [InlineData("524946462400000057454250", ImageFormat.WebP, "image/webp")]
    [InlineData("49492A00", ImageFormat.Tiff, "image/tiff")]
    [InlineData("4D4D002A", ImageFormat.Tiff, "image/tiff")]
    [InlineData("524946462400000057415645", ImageFormat.Unknown, "application/octet-stream")]
    [InlineData("424D", ImageFormat.Unknown, "application/octet-stream")]
    [InlineData("", ImageFormat.Unknown, "application/octet-stream")]
    public void Formats_AreToldApartByTheirSignatures(string hex, ImageFormat expected, string mediaType)
    {
        var format = ImageFormats.Detect(Convert.FromHexString(hex));

        Assert.Equal(expected, format);
        Assert.Equal(mediaType, ImageFormats.MediaType(format));
    }

    // ---- The size an image is sent at ----------------------------------------------------------------------------

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1024, 1024)]
    [InlineData(2048, 512)]
    [InlineData(1, 1)]
    public void AnImageWithinTheLimits_KeepsItsSize(int width, int height) =>
        Assert.Equal((width, height), ImagePreprocessingOptions.Default.FitWithin(width, height));

    [Theory]
    [InlineData(4032, 3024, 1182, 886)]
    [InlineData(3840, 2160, 1365, 768)]
    [InlineData(1025, 1024, 1024, 1023)]
    [InlineData(10000, 100, 2048, 20)]
    [InlineData(100, 30000, 6, 2048)]
    public void ALargerImage_IsScaledDownKeepingItsProportions(int width, int height, int expectedWidth, int expectedHeight)
    {
        var (fittedWidth, fittedHeight) = ImagePreprocessingOptions.Default.FitWithin(width, height);

        Assert.Equal((expectedWidth, expectedHeight), (fittedWidth, fittedHeight));
        Assert.True(ImagePreprocessingOptions.Default.IsWithin(fittedWidth, fittedHeight));
    }

    [Fact]
    public void AnySize_FitsWithinTheLimits_AndNeverLosesASide()
    {
        var random = new Random(39);
        var options = new ImagePreprocessingOptions { MaxPixels = 500_000, MaxLongSide = 1500 };
        for (var run = 0; run < 5000; run++)
        {
            var width = random.Next(1, 40_000);
            var height = random.Next(1, 40_000);

            var (fittedWidth, fittedHeight) = options.FitWithin(width, height);

            Assert.True(options.IsWithin(fittedWidth, fittedHeight), $"{width} x {height} -> {fittedWidth} x {fittedHeight}");
            Assert.InRange(fittedWidth, 1, width);
            Assert.InRange(fittedHeight, 1, height);

            // As large as the limits allow: a side two pixels longer would not fit, unless the image fitted already.
            if ((fittedWidth, fittedHeight) != (width, height))
            {
                Assert.False(options.IsWithin(fittedWidth + 2, fittedHeight + 2) && fittedWidth + 2 <= width && fittedHeight + 2 <= height);
            }
        }
    }

    // ---- The prompt ----------------------------------------------------------------------------------------------

    [Fact]
    public void AnImage_GoesAsAnImageToAModelThatReadsImages_ForTheNewestMessageOnly()
    {
        var builder = new PromptBuilder();
        var older = Image([9, 9, 9], "older words");
        var newer = Image(Pixels, "newer words");

        var built = builder.Build(null, [User("Old", older), Answer("Seen."), User("What is this?", newer)], VisionModel);

        Assert.Equal([Pixels], built.Request.Images.Select(image => image.ToArray()));
        Assert.Equal("What is this?", built.Request.Messages[^1].Text);
        Assert.Equal("Old", built.Request.Messages[0].Text);
        Assert.Empty(built.Notices);
    }

    [Fact]
    public void AnImage_GoesAsItsRecognizedText_ToAModelThatCannotReadImages_WithItsOwnNotice()
    {
        var built = new PromptBuilder().Build(null, [User("What does it say?", Image(Pixels, "Recognized words"))], TextModel);

        Assert.Empty(built.Request.Images);
        Assert.Equal(
            "<untrusted_context id=\"1\" kind=\"image_text\" name=\"photo.jpg\">\nRecognized words\n</untrusted_context>\n\nWhat does it say?",
            Assert.Single(built.Request.Messages).Text);
        Assert.Equal([PromptBuilder.ImageTextNotice], built.Notices);
    }

    [Fact]
    public void AnImageWithoutText_IsLeftOutOfATextModelsPrompt_WithItsOwnNotice_BesideAScreenshotsNotice()
    {
        var screenshot = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot") { ImageData = Pixels };

        var built = new PromptBuilder().Build(null, [User("What is this?", Image(Pixels, null), screenshot)], TextModel);

        Assert.Empty(built.Request.Images);
        Assert.Equal("What is this?", Assert.Single(built.Request.Messages).Text);
        Assert.Equal([PromptBuilder.ScreenshotDroppedNotice, PromptBuilder.ImageDroppedNotice], built.Notices);
    }

    [Fact]
    public void AnImageItemWithNoPixelsOrText_IsEmptyContext()
    {
        var empty = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "gone.png");

        var built = new PromptBuilder().Build(null, [User("And this?", empty)], VisionModel);

        Assert.Empty(built.Request.Images);
        Assert.Equal([PromptBuilder.EmptyContextNotice], built.Notices);
    }

    [Fact]
    public void AnImage_IsBudgetedAsOneFixedShare_AndMakesTheConversationHeavy()
    {
        var built = new PromptBuilder().Build(
            null, [User("Describe it", Image(Pixels, null))], VisionModel, new Settings.ContextLimitSettings());

        Assert.Single(built.Request.Images);
        Assert.Equal(Budgeting.ContextBudgetMode.Heavy, built.Budget!.Mode);
        Assert.True(built.Budget.EstimatedPromptTokens >= Budgeting.ContextBudgeter.ImageTokens);
    }

    // ---- The orchestrator ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheModelGetsThePreparedImage_WhileTheConversationKeepsTheOriginal()
    {
        var model = new ScriptedModel { Active = VisionModel };
        model.Write("A cat.");
        model.End();
        var images = new FakeImagePreprocessor();
        var original = Pixels.ToArray();
        var item = Image(original, null);
        var session = ConversationSession.Start(new TestClock(Now));

        var chunks = await ReadAllAsync(Orchestrator(model, images).AskAsync(session, "What is this?", [item]));

        Assert.Equal(["A cat."], chunks.Select(chunk => chunk.Text));
        Assert.Equal([Pixels], images.Given);
        var sent = Assert.Single(Assert.Single(model.Requests).Images).ToArray();
        Assert.Equal(Pixels[0] ^ 0xFF, sent[0]);
        Assert.Equal(Pixels[1..], sent[1..]);

        // The original is untouched, and it is what the conversation holds.
        Assert.Equal(Pixels, original);
        var asked = session.Conversation.Messages[0];
        Assert.Equal(Pixels, Assert.Single(asked.ContextItems).ImageData.ToArray());
    }

    [Fact]
    public async Task AnImageThatCannotBeRead_IsLeftOutWithANotice_AndTheQuestionIsStillAsked()
    {
        var model = new ScriptedModel { Active = VisionModel };
        model.End();
        var unreadable = Image("BAD image"u8.ToArray(), null);
        var readable = Image(Pixels, null);

        var chunks = await ReadAllAsync(Orchestrator(model, new FakeImagePreprocessor())
            .AskAsync(ConversationSession.Start(new TestClock(Now)), "Compare them", [unreadable, readable]));

        Assert.Equal([AssistantOrchestrator.UnreadableImageNotice], chunks.Select(chunk => chunk.Text));
        Assert.Single(Assert.Single(model.Requests).Images);
    }

    [Fact]
    public async Task ATextModel_IsNeverSentImages_SoNothingIsPrepared()
    {
        var model = new ScriptedModel { Active = TextModel };
        model.End();
        var images = new FakeImagePreprocessor();

        await ReadAllAsync(Orchestrator(model, images)
            .AskAsync(ConversationSession.Start(new TestClock(Now)), "What is this?", [Image(Pixels, "Words")]));

        Assert.Empty(images.Given);
        Assert.Empty(Assert.Single(model.Requests).Images);
    }

    [Fact]
    public async Task PreparingImages_LogsCountsAndSizes_NeverTheImageOrItsName()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var model = new ScriptedModel { Active = VisionModel };
        model.End();
        var item = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "PRIVATE-NAME.png") { ImageData = Pixels };
        var orchestrator = new AssistantOrchestrator(
            model, new FixedSettings(), new PromptBuilder(), new FakeImagePreprocessor(), new TestClock(Now),
            loggers.CreateLogger<AssistantOrchestrator>());

        await ReadAllAsync(orchestrator.AskAsync(ConversationSession.Start(new TestClock(Now)), "PRIVATE-QUESTION", [item]));

        Assert.Contains("Images prepared: 1 sent (1 resized, 0 unreadable left out), 11 bytes given", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Pixels), capture.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProtocolRequest_CarriesThePreparedImages()
    {
        var request = new ModelRequest("Instructions", [new Message(Guid.NewGuid(), MessageRole.User, "What is this?", Now)])
        {
            Images = [Pixels],
        };

        var generation = Assert.IsType<GenerateMultimodalRequest>(LocalModelService.ToGenerationRequest("vision-model", request));

        Assert.Equal([Pixels], generation.Images.Select(image => image.ToArray()));
    }

    private static AssistantOrchestrator Orchestrator(IModelService model, IImagePreprocessor images) =>
        new(model, new FixedSettings(), new PromptBuilder(), images, new TestClock(Now), NullLogger<AssistantOrchestrator>.Instance);

    private static ContextItem Image(byte[] pixels, string? text) =>
        new(Guid.NewGuid(), ContextItemType.Image, "photo.jpg") { ImageData = pixels, Text = text };

    private static Message User(string text, params ContextItem[] context) =>
        new(Guid.NewGuid(), MessageRole.User, text, Now) { ContextItems = context };

    private static Message Answer(string text) => new(Guid.NewGuid(), MessageRole.Assistant, text, Now);

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
