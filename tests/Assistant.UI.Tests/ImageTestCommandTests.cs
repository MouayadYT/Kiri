using System.IO;
using System.Windows;
using Assistant.UI.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Permissions;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The developer's image test: "demo image" sends one image and one question to the local model ------------------

    [Theory]
    [InlineData("demo image", DemoAnswerProvider.DefaultImageQuestion)]
    [InlineData("  Demo  Image  ", DemoAnswerProvider.DefaultImageQuestion)]
    [InlineData("demo image?", DemoAnswerProvider.DefaultImageQuestion)]
    [InlineData("demo image What does the sign say?", "What does the sign say?")]
    [InlineData("DEMO IMAGE: how many cats?", "how many cats?")]
    [InlineData("demo images", null)]
    [InlineData("demo imagery", null)]
    [InlineData("show me a demo image", null)]
    [InlineData("demo", null)]
    public void TheImageTest_IsDemoImage_OptionallyFollowedByTheQuestion(string typed, string? question) =>
        Assert.Equal(question, DemoAnswerProvider.ImageQuestion(typed));

    [Fact]
    public void DemoImage_SendsThePickedImageAndTheQuestion_ThroughThePipeline_AndShowsTheImage() => RunSta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), $"assistant-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png(1600, 1200));
        var original = File.ReadAllBytes(file);
        try
        {
            var model = new ScriptedModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = true } };
            var demo = ImageTest(model, new FixedPicker(file));
            var shown = new List<MessageViewModel>();

            var asked = demo.StreamAnswerAsync("demo image How many squares?", shown.Add, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The local model was not asked.");
            model.Write(AssistantResponseChunk.ForTextDelta("Four."));
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            // The model got the question and the image, scaled down to the limits.
            var request = Assert.Single(model.Requests);
            Assert.Equal("How many squares?", request.Messages[^1].Text);
            var sent = Assert.Single(request.Images).ToArray();
            Assert.Equal(ImageFormat.Png, ImageFormats.Detect(sent));
            Assert.True(sent.Length < original.Length);

            // The answer shows the image, then what the model said; the file was only read.
            var answer = Assert.Single(shown);
            Assert.Equal(MessageStatus.Complete, answer.Status);
            var shownImage = Assert.Single(Assert.IsType<ImageCollection>(answer.Content[0]).Images);
            Assert.Equal(file, shownImage.Path);
            Assert.Equal(Path.GetFileName(file), shownImage.Name);
            Assert.Equal("Four.", answer.Text);
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void DemoImage_AsksWhatIsInTheImage_WhenNoQuestionFollows_AndASmallImageGoesAsItIs() => RunSta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), $"assistant-image-test-{Guid.NewGuid():N}.png");
        var image = Png(200, 100);
        File.WriteAllBytes(file, image);
        try
        {
            var model = new ScriptedModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = true } };
            var shown = new List<MessageViewModel>();

            var asked = ImageTest(model, new FixedPicker(file)).StreamAnswerAsync("demo image", shown.Add, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The local model was not asked.");
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            var request = Assert.Single(model.Requests);
            Assert.Equal(DemoAnswerProvider.DefaultImageQuestion, request.Messages[^1].Text);
            Assert.Equal(image, Assert.Single(request.Images).ToArray());
            Assert.IsType<ImageCollection>(Assert.Single(shown).Content[0]);
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void DemoImage_AttachesTheImageToTheQuestion_SoTheHistoryCardShowsIt() => RunSta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), $"assistant-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png(200, 100));
        try
        {
            var model = new ScriptedModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = true } };
            var question = new MessageViewModel(MessageRole.User, "demo image What is this?");
            var changed = new List<string?>();
            question.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            var shown = new List<MessageViewModel>();

            var asked = ImageTest(model, new FixedPicker(file))
                .StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The local model was not asked.");
            model.Write(AssistantResponseChunk.ForTextDelta("A picture."));
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            var attached = Assert.Single(question.Attachments);
            Assert.Equal(file, attached.Path);
            Assert.Contains(nameof(MessageViewModel.Attachments), changed);
            Assert.Equal("demo image What is this?", question.Text);

            var card = new HistoryConversationViewModel(Guid.NewGuid(), [question, .. shown], Now, new FixedClock(Now));
            Assert.Same(attached, card.Image);
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void AnAttachedImage_SitsAboveTheUsersBubbleOnTheRight_AtItsOwnProportions() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var tall = new System.Windows.Media.Imaging.WriteableBitmap(52, 110, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var wide = new System.Windows.Media.Imaging.WriteableBitmap(800, 400, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        Fill(tall, 0xFF3A7BD5);
        Fill(wide, 0xFFE0A030);
        tall.Freeze();
        wide.Freeze();
        var question = new MessageViewModel(MessageRole.User, "What's in this picture?");
        model.StartNew("placeholder");
        model.Messages.Clear();
        model.Messages.Add(question);
        try
        {
            panel.ShowConversation();
            Pump();
            question.Attach(new ImageItem("tall.png", tall));
            question.Attach(new ImageItem("wide.png", wide));
            Pump();

            var images = Descendants<System.Windows.Controls.Image>(panel)
                .Where(image => image.Source == tall || image.Source == wide).ToList();
            Assert.Equal(2, images.Count);
            var bubble = Assert.Single(Descendants<SpeechBubble>(panel));
            var bubbleRight = bubble.TransformToAncestor(panel).Transform(new Point(bubble.ActualWidth, 0)).X;

            // The tall one at its own size, the wide one scaled down to a bubble's width, both keeping proportions.
            Assert.Equal(52, images[0].ActualWidth, 1);
            Assert.Equal(110, images[0].ActualHeight, 1);
            Assert.Equal(2, images[1].ActualWidth / images[1].ActualHeight, 2);
            Assert.True(images[1].ActualWidth <= 286.5);
            foreach (var image in images)
            {
                var right = image.TransformToAncestor(panel).Transform(new Point(image.ActualWidth, 0)).X;
                Assert.Equal(bubbleRight, right, 1);
            }

            RenderGlass(panel, "user-attachment-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void AskingInThePanel_HandsTheProviderTheQuestionMessageItself() => RunSta(() =>
    {
        var answers = new RecordingQuestionAnswers();
        var conversation = CreateConversationModel(answers: answers);

        conversation.StartNew("First");
        WaitUntil(() => answers.Asked.Count == 1, "The question was not asked.");

        Assert.Same(conversation.Messages[0], answers.Asked[0]);
    });

    [Fact]
    public void DemoImage_WithAModelThatCannotSee_SaysSo_AndAsksTheQuestionAlone() => RunSta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), $"assistant-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png(64, 64));
        try
        {
            var model = new ScriptedModel();
            var shown = new List<MessageViewModel>();

            var asked = ImageTest(model, new FixedPicker(file)).StreamAnswerAsync("demo image", shown.Add, CancellationToken.None);
            WaitUntil(() => model.Requests.Count == 1, "The local model was not asked.");
            model.End();
            WaitUntil(() => asked.IsCompleted, "The answer did not end.");

            Assert.Empty(Assert.Single(model.Requests).Images);
            Assert.Contains(Assert.Single(shown).Content.OfType<TextContent>(), part => part.Text == Core.Orchestration.PromptBuilder.ImageDroppedNotice);
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void DemoImage_WithoutAnImageChosen_OrWithAFileThatIsNotAnImage_AsksNothing() => RunSta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), $"assistant-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllText(file, "not an image");
        try
        {
            var model = new ScriptedModel();
            var shown = new List<MessageViewModel>();

            var cancelled = ImageTest(model, new FixedPicker(null)).StreamAnswerAsync("demo image", shown.Add, CancellationToken.None);
            WaitUntil(() => cancelled.IsCompleted, "Choosing nothing did not end the test.");
            var unreadable = ImageTest(model, new FixedPicker(file)).StreamAnswerAsync("demo image", shown.Add, CancellationToken.None);
            WaitUntil(() => unreadable.IsCompleted, "An unreadable file did not end the test.");

            Assert.Empty(model.Requests);
            Assert.Equal(
                ["No image was chosen, so nothing was asked.", "That file isn't an image the Assistant can read."],
                shown.Select(message => message.Text));
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void TheAppWiresTheImageTest_ToTheRealPreprocessorAndAFilePicker()
    {
        using var host = AppHost.Create();

        Assert.IsType<ImagePreprocessor>(host.Services.GetRequiredService<IImagePreprocessor>());
        Assert.IsType<OpenFileImagePicker>(host.Services.GetRequiredService<IImagePicker>());
        var answers = Assert.IsType<DemoAnswerProvider>(host.Services.GetRequiredService<IAnswerProvider>());
        Assert.Contains("demo image", answers.Answer("demo")!.Text, StringComparison.Ordinal);
    }

    // The permission policy is the real one over the settings given (the defaults, which allow Files, when none are).
    private static DemoAnswerProvider ImageTest(IModelService model, IImagePicker picker, ISettingsService? settings = null) =>
        new(new FakeClipboard(), new FixedClock(Now), localModel: LocalAnswers(model), imagePicker: picker,
            images: new ImagePreprocessor(), permissions: new SettingsPermissionPolicy(settings ?? new InMemorySettingsService()));

    // A PNG of colored squares, drawn by the Windows encoder.
    private static byte[] Png(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            var (x, y) = (index % width, index / width);
            bgra[(index * 4) + (((x * 2 / width) + (y * 2 / height)) % 3)] = 255;
            bgra[(index * 4) + 3] = 255;
        }

        return Task.Run(async () =>
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = stream.GetInputStreamAt(0).AsStreamForRead();
            await reader.ReadExactlyAsync(bytes);
            return bytes;
        }).GetAwaiter().GetResult();
    }

    private static void Fill(System.Windows.Media.Imaging.WriteableBitmap bitmap, uint color)
    {
        var pixels = Enumerable.Repeat(color, bitmap.PixelWidth * bitmap.PixelHeight).ToArray();
        bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, bitmap.PixelWidth * 4, 0);
    }

    private sealed class RecordingQuestionAnswers : IAnswerProvider
    {
        public List<MessageViewModel> Asked { get; } = [];

        public MessageViewModel? Answer(string question) => null;

        public Task StreamAnswerAsync(
            Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
        {
            Asked.Add(question);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedPicker(string? path) : IImagePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult(path);
    }
}
