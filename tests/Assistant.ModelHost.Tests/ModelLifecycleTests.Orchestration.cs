using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// A chat turn from the orchestrator down to the engine: the orchestrator over the <see cref="Assistant.Core.ModelHosting.LocalModelService"/>,
/// the lifecycle, the client and a real session, with the fake engine answering over its socket.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    [Fact]
    public async Task AChatTurn_ReachesTheEngineAsThePromptTheBuilderMade_AndAFollowUpCarriesTheEarlierTurns()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var orchestrator = new AssistantOrchestrator(
            CreateService(lifecycle, setup.ScenarioPath),
            new FixedSettings(new AppSettings()),
            new PromptBuilder(),
            new PassThroughImages(),
            TimeProvider.System,
            NullLogger<AssistantOrchestrator>.Instance);
        var session = ConversationSession.Start(TimeProvider.System);
        var selection = new ContextItem(Guid.NewGuid(), ContextItemType.Selection, "Notes") { Text = "The selected words" };

        var first = await ReadAllAsync(
            orchestrator.AskAsync(session, "What is this?", [selection], cancellationToken: TestPipes.Timeout()));
        var second = await ReadAllAsync(
            orchestrator.AskAsync(session, "And then?", cancellationToken: TestPipes.Timeout()));

        Assert.Equal("Hello there!", string.Concat(first.Select(chunk => chunk.Text)));
        Assert.Equal("Hello there!", string.Concat(second.Select(chunk => chunk.Text)));

        // The first prompt: the instructions, then the question with its context wrapped as untrusted.
        var firstPrompt = SentMessages(setup, 1);
        Assert.Equal(["system", "user"], firstPrompt.Select(message => message.Role));
        Assert.StartsWith(AssistantInstructions.Default, firstPrompt[0].Content, StringComparison.Ordinal);
        Assert.EndsWith(AssistantInstructions.UntrustedContextGuidance, firstPrompt[0].Content, StringComparison.Ordinal);
        Assert.Equal(
            "<untrusted_context id=\"1\" kind=\"selection\" name=\"Notes\">\nThe selected words\n</untrusted_context>\n\nWhat is this?",
            firstPrompt[1].Content);

        // The follow-up's prompt starts with the first one, then the answer, then the new question.
        var secondPrompt = SentMessages(setup, 2);
        Assert.Equal(["system", "user", "assistant", "user"], secondPrompt.Select(message => message.Role));
        Assert.Equal(firstPrompt.Select(message => message.Content), secondPrompt.Take(2).Select(message => message.Content));
        Assert.Equal(["Hello there!", "And then?"], secondPrompt.Skip(2).Select(message => message.Content));

        // And the conversation holds all four messages.
        Assert.Equal(
            [(MessageRole.User, "What is this?"), (MessageRole.Assistant, "Hello there!"),
                (MessageRole.User, "And then?"), (MessageRole.Assistant, "Hello there!")],
            session.Conversation.Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(1, setup.Launches);
    }

    [Fact]
    public async Task AChatTurnWithoutAModelSetUp_IsNotStartedAndLeavesTheConversationAlone()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var orchestrator = new AssistantOrchestrator(
            CreateService(lifecycle, null),
            new FixedSettings(new AppSettings()),
            new PromptBuilder(),
            new PassThroughImages(),
            TimeProvider.System,
            NullLogger<AssistantOrchestrator>.Instance);
        var session = ConversationSession.Start(TimeProvider.System);

        await Assert.ThrowsAsync<ModelNotSetUpException>(
            () => ReadAllAsync(orchestrator.AskAsync(session, "Hello", cancellationToken: TestPipes.Timeout())));

        Assert.Empty(session.Conversation.Messages);
        Assert.Equal(0, host.Launcher.Started);
    }

    [Fact]
    public async Task AnImageTurn_ReachesTheEngineAsTheImageThenTheQuestion_AndTheConversationKeepsTheOriginal()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var settings = new AppSettings
        {
            Model = new ModelSettings { ModelFilePath = setup.ScenarioPath, ProjectorFilePath = setup.CreateGguf("mmproj.gguf") },
        };
        var images = new PassThroughImages();
        var orchestrator = new AssistantOrchestrator(
            new LocalModelService(lifecycle, new FixedSettings(settings), new NoModelProfiles(), NullLogger<LocalModelService>.Instance),
            new FixedSettings(settings),
            new PromptBuilder(),
            images,
            TimeProvider.System,
            NullLogger<AssistantOrchestrator>.Instance);
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "photo.png") { ImageData = SampleMessages.PrivateImage };

        var answer = await ReadAllAsync(
            orchestrator.AskAsync(ConversationSession.Start(TimeProvider.System), "What is this?", [photo], cancellationToken: TestPipes.Timeout()));

        Assert.Equal("Hello there!", string.Concat(answer.Select(chunk => chunk.Text)));
        Assert.Equal(1, images.Prepared);
        using var sent = JsonDocument.Parse(File.ReadAllBytes(FakeChatReply.RequestPath(setup.ScenarioPath, 1)));
        var parts = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(SampleMessages.PrivateImage),
            parts[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("What is this?", parts[1].GetProperty("text").GetString());
    }

    private static List<(string Role, string Content)> SentMessages(FakeEngineSetup setup, int request)
    {
        using var sent = JsonDocument.Parse(File.ReadAllBytes(FakeChatReply.RequestPath(setup.ScenarioPath, request)));
        return sent.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => (message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!))
            .ToList();
    }

    /// <summary>Sends every image as it is, counting them.</summary>
    private sealed class PassThroughImages : IImagePreprocessor
    {
        public int Prepared { get; private set; }

        public Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
        {
            Prepared++;
            return Task.FromResult(
                new PreparedImage(image, ImageFormats.Detect(image.Span), 1, 1, 1, 1) { IsOriginal = true });
        }
    }
}
