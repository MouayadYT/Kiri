using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.Data;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Streamed answers: the local model's answer growing in the conversation ------------------------------------

    [Fact]
    public void StreamedTextIsShownInBatches_AtMostOncePerDispatcherTurn() => RunSta(() =>
    {
        var content = new TextContent();
        var redraws = 0;
        ((INotifyPropertyChanged)content).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextContent.Text)) redraws++;
        };
        var text = new StreamingText(content);

        foreach (var piece in new[] { "Once", " upon", " a", " time" })
        {
            text.Append(piece);
        }

        // Nothing is redrawn while the pieces arrive: they are shown together when the dispatcher is free.
        Assert.Equal("", content.Text);
        Assert.True(text.IsPending);
        Pump();
        Assert.Equal("Once upon a time", content.Text);
        Assert.Equal(1, redraws);
        Assert.False(text.IsPending);

        // A flush shows what has gathered at once, and nothing is redrawn twice for it.
        text.Append(".");
        text.Flush();
        Assert.Equal("Once upon a time.", content.Text);
        Pump();
        Assert.Equal(2, redraws);
    });

    [Fact]
    public void TheModelsAnswerAppearsWithItsFirstWords_AndGrowsAsTheModelWrites() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var answers = LocalAnswers(model);
        var shown = new List<MessageViewModel>();

        var answering = answers.StreamAnswerAsync("Why is the sky blue?", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        Assert.Empty(shown);

        model.Write(AssistantResponseChunk.ForTextDelta("Because "));
        WaitUntil(() => shown.Count == 1, "The answer did not appear with its first words.");
        var prose = Assert.IsType<TextContent>(Assert.Single(shown[0].Content));
        Assert.Equal(MessageRole.Assistant, shown[0].Role);
        Assert.Equal("Because ", prose.Text);

        model.Write(AssistantResponseChunk.ForTextDelta("light "));
        model.Write(AssistantResponseChunk.ForTextDelta("scatters."));
        WaitUntil(() => prose.Text == "Because light scatters.", "The answer did not grow.");
        model.End();
        WaitUntil(() => answering.IsCompleted, "The answer did not end.");

        Assert.True(answering.IsCompletedSuccessfully);
        Assert.Single(shown);
        Assert.Equal(MessageStatus.Complete, shown[0].Status);

        // The question goes to the model alone, with the orchestrator's default instructions.
        var request = Assert.Single(model.Requests);
        Assert.StartsWith(AssistantInstructions.Default, request.Instructions, StringComparison.Ordinal);
        var message = Assert.Single(request.Messages);
        Assert.Equal((MessageRole.User, "Why is the sky blue?"), (message.Role, message.Text));
    });

    [Fact]
    public void ANoticeIsAParagraphOfItsOwn_AndAFailureIsSaidInPlainWords() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var shown = new List<MessageViewModel>();
        var answering = LocalAnswers(model)
            .StreamAnswerAsync("Write a long essay", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("The essay"));
        model.Write(AssistantResponseChunk.ForNotice(LocalModelService.OutputLimitNotice));
        model.End();
        WaitUntil(() => answering.IsCompleted, "The answer did not end.");

        Assert.Equal(
            ["The essay", LocalModelService.OutputLimitNotice],
            Assert.Single(shown).Content.Cast<TextContent>().Select(part => part.Text));

        var failing = new ScriptedModel();
        var failed = new List<MessageViewModel>();
        var failure = LocalAnswers(failing)
            .StreamAnswerAsync("Summarize this book", failed.Add, CancellationToken.None);
        WaitUntil(() => failing.Requests.Count == 1, "The model was not asked.");
        failing.Write(AssistantResponseChunk.ForTextDelta("It is"));
        failing.End(new ModelHostException(ModelHostErrorCode.ContextExceeded));
        WaitUntil(() => failure.IsCompleted, "The failed answer did not end.");

        Assert.True(failure.IsCompletedSuccessfully);
        Assert.Equal(MessageStatus.Failed, Assert.Single(failed).Status);
        Assert.Equal(
            ["It is", ModelErrorText.Describe(new ModelHostException(ModelHostErrorCode.ContextExceeded))],
            Assert.Single(failed).Content.Cast<TextContent>().Select(part => part.Text));
    });

    [Fact]
    public void WithoutAModel_TheAnswerSaysHowToSetOneUp() => RunSta(() =>
    {
        var model = new ScriptedModel { Active = null };
        var shown = new List<MessageViewModel>();

        var answering = LocalAnswers(model)
            .StreamAnswerAsync("Hello", shown.Add, CancellationToken.None);
        WaitUntil(() => answering.IsCompleted, "The answer did not end.");

        Assert.Equal(ModelAnswerProvider.NoModelText, Assert.Single(shown).Text);
        Assert.Contains("demo model", ModelAnswerProvider.NoModelText, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    });

    [Fact]
    public void EscStopsAStreamingAnswer_KeepingWhatItSaid_ThenClosesThePanel() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var conversation = CreateConversationModel(answers: LocalAnswers(model));

        conversation.StartNew("Tell me a story");
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        Assert.True(conversation.IsAnswering);
        model.Write(AssistantResponseChunk.ForTextDelta("Once"));
        WaitUntil(() => conversation.Messages.Count == 2, "The answer did not appear.");

        Assert.False(conversation.HandleEscape());
        Assert.False(conversation.IsAnswering);
        Assert.True(model.Cancelled);
        WaitUntil(() => conversation.Messages[1].Status == MessageStatus.Stopped, "The answer was not marked as stopped.");
        model.Write(AssistantResponseChunk.ForTextDelta(" upon a time"));
        Pump();

        Assert.Equal("Once", conversation.Messages[1].Text);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.True(conversation.HandleEscape());
    });

    [Fact]
    public void TheStopButtonShowsWhileAnAnswerStreams_AndStopsIt_KeepingWhatItSaidMarkedAsStopped() => RunSta(() => WithTheme(() =>
    {
        var model = new ScriptedModel();
        var (panel, conversation, _) = CreatePanel(answers: LocalAnswers(model));
        conversation.StartNew("Tell me a story");
        try
        {
            panel.ShowConversation();
            var stop = Named<Button>(panel, "StopButton");
            WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
            Pump();

            // It is there from the moment the answer is asked for, at the right end of the composer, which stays to type the next message in.
            Assert.Equal(Visibility.Visible, stop.Visibility);
            Assert.Equal(Visibility.Visible, Named<Grid>(panel, "Composer").Visibility);
            Assert.True(conversation.ShowsComposer);
            Assert.False(conversation.CanCompose);
            model.Write(AssistantResponseChunk.ForTextDelta("Once upon"));
            WaitUntil(() => conversation.IsStreaming, "The answer did not start streaming.");
            WaitUntil(() => stop.IsVisible, "The Stop button did not show while the answer streamed.");

            // Inside the composer's glass, at its right end, and never over where the words are typed.
            var composer = Named<Grid>(panel, "Composer");
            var place = stop.TranslatePoint(default, composer);
            Assert.Equal(composer.ActualWidth - 5 - stop.ActualWidth, place.X, 1);
            Assert.InRange(place.Y, 0, composer.ActualHeight - stop.ActualHeight);
            Assert.Equal(new Thickness(15, 7.5, 36, 7.5), Named<PromptInputControl>(panel, "ComposerInput").Padding);
            Assert.True(conversation.IsStreaming);
            Assert.True(stop.IsEnabled);
            Assert.Equal("Stop", System.Windows.Automation.AutomationProperties.GetName(stop));
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "stop-button-2x.png", 2);

            ((IInvokeProvider)new ButtonAutomationPeer(stop).GetPattern(PatternInterface.Invoke)).Invoke();

            WaitUntil(() => conversation.Messages[1].Status == MessageStatus.Stopped, "The answer was not marked as stopped.");
            Assert.True(model.Cancelled);
            Assert.False(conversation.IsAnswering);
            Assert.False(conversation.IsStreaming);
            model.Write(AssistantResponseChunk.ForTextDelta(" a time"));
            Pump();
            Assert.False(stop.IsVisible);
            Assert.Equal("Once upon", conversation.Messages[1].Text);

            // The answer ends with a dim note saying it was stopped, not with an error.
            var note = Assert.Single(Descendants<TextBlock>(panel), text => text.Name == "StoppedNote" && text.IsVisible);
            Assert.Equal("Stopped", note.Text);
            RenderGlass(panel, "stopped-answer-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void AnAnswerStoppedBeforeItsFirstWords_IsShownAsStopped_WithNothingInIt() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var conversation = CreateConversationModel(answers: LocalAnswers(model));
        conversation.StartNew("Tell me a story");
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");

        conversation.StopCommand.Execute(null);

        WaitUntil(() => conversation.Messages.Count == 2, "The stopped answer was not shown.");
        Assert.Equal(MessageStatus.Stopped, conversation.Messages[1].Status);
        Assert.Empty(conversation.Messages[1].Content);
        Assert.False(conversation.StopCommand.CanExecute(null));

        // Stopped from the Searching chip while the model thought: the model service's own cancellation, not the
        // conversation's, which the answer ends with all the same.
        var chip = new ScriptedModel();
        var shown = new List<MessageViewModel>();
        var answering = LocalAnswers(chip)
            .StreamAnswerAsync("Hello", shown.Add, CancellationToken.None);
        WaitUntil(() => chip.Requests.Count == 1, "The model was not asked.");
        chip.Write(AssistantResponseChunk.ForTextDelta("Hi"));
        chip.End(new OperationCanceledException());
        WaitUntil(() => answering.IsCompleted, "The answer did not end.");

        Assert.True(answering.IsCompletedSuccessfully);
        Assert.Equal((MessageStatus.Stopped, "Hi"), (Assert.Single(shown).Status, shown[0].Text));
    });

    [Fact]
    public void ANewConversationStopsTheAnswerStillStreaming() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var conversation = CreateConversationModel(answers: LocalAnswers(model));
        conversation.StartNew("First question");
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("First"));
        WaitUntil(() => conversation.Messages.Count == 2, "The answer did not appear.");

        conversation.StartNew("Second question");

        WaitUntil(() => model.Cancelled, "The first answer was not stopped.");
        Pump();
        Assert.Equal(["Second question"], conversation.Messages.Select(message => message.Text));
    });

    [Fact]
    public void QuestionsWithoutASampleAnswerGoToTheLocalModel() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: LocalAnswers(model));
        var shown = new List<MessageViewModel>();

        var sample = demo.StreamAnswerAsync("demo text", shown.Add, CancellationToken.None);
        Assert.True(sample.IsCompletedSuccessfully);
        Assert.Empty(model.Requests);
        Assert.Contains("Plain answers", Assert.Single(shown).Text);

        var asked = demo.StreamAnswerAsync("What is the capital of France?", shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The local model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Paris."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");
        Assert.Equal("Paris.", shown[^1].Text);
        Assert.Contains("local model", demo.Answer("demo")!.Text);
    });

    [Fact]
    public void AGrowingAnswerIsFollowedWhileTheEndIsInView_AndLeftAloneWhenScrolledBack() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("A question with a long answer");
        var prose = new TextContent(string.Join("\n\n", Enumerable.Range(1, 30)
            .Select(i => $"Synthetic paragraph {i}, long enough to wrap across more than one line of the conversation.")));
        var answer = new MessageViewModel(MessageRole.Assistant);
        answer.Content.Add(prose);
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            var transcript = Named<FadingScrollViewer>(panel, "Transcript");
            WaitUntil(() => transcript.ScrollableHeight > 0 && transcript.VerticalOffset == transcript.ScrollableHeight,
                "The answer was not scrolled into view.");

            // More words at the end: the view follows them.
            var before = transcript.ScrollableHeight;
            prose.Text += "\n\nAnother synthetic paragraph, streamed in after the others were shown.";
            WaitUntil(() => transcript.ScrollableHeight > before && transcript.VerticalOffset == transcript.ScrollableHeight,
                "The growing answer was not followed.");

            // Scrolled back to read: it stays there while the answer grows.
            transcript.ScrollToVerticalOffset(100);
            Pump();
            before = transcript.ScrollableHeight;
            prose.Text += "\n\nYet another synthetic paragraph, streamed in while the reader was scrolled back.";
            WaitUntil(() => transcript.ScrollableHeight > before, "The answer did not grow.");
            Pump();
            Assert.Equal(100, transcript.VerticalOffset);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheAppAsksTheLocalModelThroughTheModelHost()
    {
        // Building the host validates every registration.
        using var host = AppHost.Create();
        Assert.IsType<ActivityModelService>(host.Services.GetRequiredService<IModelService>());
        Assert.NotNull(host.Services.GetRequiredService<LocalModelService>());
        Assert.NotNull(host.Services.GetRequiredService<ModelAnswerProvider>());

        // Every prompt is built by the orchestrator, which asks the model service the app wires up.
        Assert.IsType<AssistantOrchestrator>(host.Services.GetRequiredService<IAssistantOrchestrator>());
        Assert.IsType<SqliteConversationService>(host.Services.GetRequiredService<IConversationService>());
    }

    // The provider over the real orchestrator, which builds the prompt, asking the scripted model.
    private static ModelAnswerProvider LocalAnswers(IModelService model, IPermissionPolicy? permissions = null) => new(
        new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now), NullLogger<AssistantOrchestrator>.Instance),
        new FixedClock(Now), permissions);

    /// <summary>A model whose answer the test writes, piece by piece.</summary>
    private sealed class ScriptedModel : IModelService
    {
        private readonly Channel<AssistantResponseChunk> _chunks = Channel.CreateUnbounded<AssistantResponseChunk>();
        private Exception? _failure;

        public ModelInfo? Active { get; init; } = new("test-model", 2048);

        public List<ModelRequest> Requests { get; } = [];

        public bool Cancelled { get; private set; }

        public void Write(AssistantResponseChunk chunk) => _chunks.Writer.TryWrite(chunk);

        public void End(Exception? failure = null)
        {
            _failure = failure;
            _chunks.Writer.TryComplete();
        }

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            using var registration = cancellationToken.Register(() => Cancelled = true);
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }

            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }
}
