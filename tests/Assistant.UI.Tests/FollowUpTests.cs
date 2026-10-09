using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Following up: asking again in the same conversation, in the floating panel and in the History window -----

    [Fact]
    public void AFollowUpIsAnsweredWithTheEarlierTurnsOfItsConversation_AndANewConversationStartsWithNone() => RunSta(() =>
    {
        var model = new ReplyingModel("Paris.", "About two million.", "Rome.");
        var conversation = CreateConversationModel(answers: LocalAnswers(model));

        conversation.StartNew("Capital of France?");
        WaitUntil(() => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The first answer did not end.");
        var id = conversation.Id;
        Assert.True(conversation.Ask("How many people live there?"));
        WaitUntil(() => conversation.Messages.Count == 4 && !conversation.IsAnswering, "The follow-up was not answered.");

        Assert.Equal(id, conversation.Id);
        Assert.Equal(
            [(MessageRole.User, "Capital of France?"), (MessageRole.Assistant, "Paris."),
                (MessageRole.User, "How many people live there?"), (MessageRole.Assistant, "About two million.")],
            conversation.Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(
            [(MessageRole.User, "Capital of France?"), (MessageRole.Assistant, "Paris."),
                (MessageRole.User, "How many people live there?")],
            model.Requests[1].Messages.Select(message => (message.Role, message.Text)));

        // Another conversation is a new one: it is asked with none of that.
        conversation.StartNew("Capital of Italy?");
        WaitUntil(() => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The new answer did not end.");
        Assert.NotEqual(id, conversation.Id);
        Assert.Equal(["Capital of Italy?"], model.Requests[2].Messages.Select(message => message.Text));
    });

    [Fact]
    public void TheComposerShowsWhenTheConversationCanTakeAMessage_NotWhileAnAnswerComesOrTheMicrophoneIsOn() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var microphone = new FakeMicrophone();
        var conversation = CreateConversationModel(microphone, LocalAnswers(model));
        var changes = new List<string?>();
        conversation.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(conversation.CanCompose);
        conversation.StartNew("Hello");
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        Assert.True(conversation.IsAnswering);
        Assert.False(conversation.CanCompose);

        model.Write(AssistantResponseChunk.ForTextDelta("Hi"));
        model.End();
        WaitUntil(() => !conversation.IsAnswering, "The answer did not end.");
        Assert.True(conversation.CanCompose);
        Assert.Contains(nameof(ConversationViewModel.CanCompose), changes);

        conversation.Voice.Start();
        Assert.True(conversation.Voice.IsListening);
        Assert.False(conversation.CanCompose);
        conversation.Voice.Stop();
        Assert.True(conversation.CanCompose);
    });

    [Fact]
    public void NothingIsAskedWhileAnAnswerIsOnItsWay() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var conversation = CreateConversationModel(answers: LocalAnswers(model));
        conversation.StartNew("First");
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        conversation.Draft = "Second";

        Assert.False(conversation.AskCommand.CanExecute(null));
        Assert.False(conversation.Ask("Second"));
        conversation.AskCommand.Execute(null);

        Assert.Single(conversation.Messages);
        Assert.Equal("Second", conversation.Draft);
        conversation.Stop();
    });

    [Fact]
    public void TheAskCommandSendsTheDraftAsTheNextMessage_AndEscapeClearsWhatWasTypedBeforeItClosesThePanel() => RunSta(() =>
    {
        var conversation = CreateConversationModel(answers: new FakeAnswers { Reply = question => new MessageViewModel(MessageRole.Assistant, "Re: " + question) });
        Assert.False(conversation.AskCommand.CanExecute(null));

        conversation.StartNew("first");
        var id = conversation.Id;
        Assert.False(conversation.AskCommand.CanExecute(null));
        conversation.Draft = "   ";
        Assert.False(conversation.AskCommand.CanExecute(null));
        conversation.Draft = "second";
        Assert.True(conversation.AskCommand.CanExecute(null));
        conversation.AskCommand.Execute(null);

        Assert.Equal("", conversation.Draft);
        Assert.Equal(id, conversation.Id);
        Assert.Equal(
            ["first", "Re: first", "second", "Re: second"],
            conversation.Messages.Select(message => message.Text));

        // Esc first throws away what was typed, and only then closes the panel.
        conversation.Draft = "half a thought";
        Assert.False(conversation.HandleEscape());
        Assert.Equal("", conversation.Draft);
        Assert.True(conversation.HandleEscape());

        // A new conversation starts with an empty composer.
        conversation.Draft = "left over";
        conversation.StartNew("third");
        Assert.Equal("", conversation.Draft);
    });

    [Fact]
    public void AnAnswerStoppedBeforeItsFirstWords_DoesNotBlockTheNextQuestionOfTheConversation() => RunSta(() =>
    {
        var model = new ReplyingModel("Fine.");
        var conversation = CreateConversationModel(answers: LocalAnswers(model));
        conversation.StartNew("One");
        conversation.Stop();

        // Straight after stopping, before the model's turn has wound up.
        Assert.True(conversation.Ask("Two"));
        WaitUntil(() => conversation.Messages.Any(message => message.Text == "Fine.") && !conversation.IsAnswering,
            "The next question was not answered.");
        Assert.DoesNotContain(conversation.Messages, message => message.Status == MessageStatus.Failed);
    });

    [Fact]
    public void TheHistoryWindowContinuesTheOpenConversation_WithItsEarlierTurns_AndTheCardFollows() => RunSta(() =>
    {
        var model = new ReplyingModel("Paris.", "About two million.");
        var answers = LocalAnswers(model);
        var floating = CreateConversationModel(answers: answers);
        floating.StartNew("Capital of France?");
        WaitUntil(() => floating.Messages.Count == 2 && !floating.IsAnswering, "The first answer did not end.");
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, answers);
        var card = history.Open(floating.Id, floating.Messages, floating.UpdatedAt);
        history.Open(new Guid("00000000-0000-0000-0000-000000000001"), [new MessageViewModel(MessageRole.User, "Newer")], ReferenceNow);
        Assert.NotSame(card, history.Conversations[0]);
        history.Selected = card;
        history.Draft = "How many people live there?";
        Assert.True(history.SendCommand.CanExecute(null));

        history.SendCommand.Execute(null);

        Assert.Equal("", history.Draft);
        WaitUntil(() => card.Messages.Count == 4 && !history.IsAnswering, "The follow-up was not answered.");
        Assert.Equal(
            ["Capital of France?", "Paris.", "How many people live there?", "About two million."],
            card.Messages.Select(message => message.Text));
        Assert.Equal(
            ["Capital of France?", "Paris.", "How many people live there?"],
            model.Requests[1].Messages.Select(message => message.Text));
        Assert.Equal("About two million.", card.Preview);
        Assert.Same(card, history.Conversations[0]);
    });

    [Fact]
    public void TheHistoryWindowSendsNothingWithoutAnOpenConversationOrAnswers_OrWhileAnAnswerComes() => RunSta(() =>
    {
        var none = new HistoryViewModel(new FixedClock(ReferenceNow));
        none.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi")], ReferenceNow);
        none.Draft = "text";
        Assert.False(none.SendCommand.CanExecute(null));
        Assert.False(none.Send("text"));

        var model = new ScriptedModel();
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(model));
        history.Draft = "text";
        Assert.False(history.SendCommand.CanExecute(null));
        history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi")], ReferenceNow);
        Assert.True(history.SendCommand.CanExecute(null));
        Assert.True(history.Send("text"));
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        Assert.True(history.IsAnswering);
        Assert.False(history.Send("again"));
        Assert.True(history.StopCommand.CanExecute(null));

        history.Stop();
        WaitUntil(() => !history.IsAnswering, "The answer did not stop.");
        Assert.Equal(MessageStatus.Stopped, history.Selected!.Messages[^1].Status);
    });

    [Fact]
    public void ThePanelsComposerShowsOnceTheAnswerIsDone_AndEnterSendsWhatWasTyped() => RunSta(() => WithTheme(() =>
    {
        var model = new ReplyingModel("First.", "Second.");
        var (panel, conversation, _) = CreatePanel(answers: LocalAnswers(model));
        conversation.StartNew("Question");
        try
        {
            panel.ShowConversation();
            var composer = Named<Grid>(panel, "Composer");
            var input = Named<PromptInputControl>(panel, "ComposerInput");
            WaitUntil(() => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The answer did not end.");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            Assert.True(composer.IsVisible);
            Assert.Equal("Ask a follow-up", input.Placeholder);
            RenderGlass(panel, "follow-up-composer-2x.png", 2);

            input.Text = "And then?";
            Assert.Equal("And then?", conversation.Draft);
            Assert.True(input.HandleEnter(ModifierKeys.None, isRepeat: false));

            WaitUntil(() => conversation.Messages.Count == 4 && !conversation.IsAnswering, "The follow-up was not answered.");
            Assert.Equal("", conversation.Draft);
            Assert.Equal("", input.Text);
            Assert.Equal(["Question", "First.", "And then?"], model.Requests[1].Messages.Select(message => message.Text));

            // While the microphone is on the composer gives its place, and the voice button offers the keyboard.
            conversation.Voice.Start();
            Pump();
            Assert.False(composer.IsVisible);
            conversation.Voice.Stop();
            Pump();
            Assert.True(composer.IsVisible);

            // Shift+Enter starts a new line instead of sending.
            input.Text = "one";
            Assert.False(input.HandleEnter(ModifierKeys.Shift, isRepeat: false));
            Assert.Equal("one", conversation.Draft);
            Assert.Equal(4, conversation.Messages.Count);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheHistoryWindowsComposerSendsOnEnter_AndItsMicrophoneDiscBecomesStopWhileAnAnswerComes() => RunSta(() => WithTheme(() =>
    {
        var model = new ScriptedModel();
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(model));
        var card = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi"),
            new MessageViewModel(MessageRole.Assistant, "Hello.")], ReferenceNow);
        var window = new HistoryWindow(history, new FakeFrameFactory(), new FakePlacement(), new MenuBackdrops())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        try
        {
            window.Show();
            Pump();
            var input = Named<PromptInputControl>(window, "ComposerInput");
            var voice = Named<Button>(window, "ComposerVoiceButton");
            Assert.Equal("Use microphone", AutomationProperties.GetName(voice));

            input.Text = "Tell me more";
            Assert.Equal("Tell me more", history.Draft);
            Assert.True(input.TrySubmit());

            WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
            Assert.Equal("", input.Text);
            // A conversation the model was never part of (one opened from the saved history) is told to it again, so the new message is
            // answered with what was said before.
            Assert.Equal(["Hi", "Hello.", "Tell me more"], model.Requests[0].Messages.Select(message => message.Text));
            Assert.Equal("Stop", AutomationProperties.GetName(voice));
            Assert.True(voice.IsEnabled);

            model.Write(AssistantResponseChunk.ForTextDelta("Sure"));
            WaitUntil(() => card.Messages.Count == 4, "The answer did not appear.");
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(voice)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();

            WaitUntil(() => !history.IsAnswering, "The answer did not stop.");
            Pump();
            Assert.Equal("Use microphone", AutomationProperties.GetName(voice));
            Assert.Equal(MessageStatus.Stopped, card.Messages[^1].Status);
            Assert.Equal("Sure", card.Messages[^1].Text);
        }
        finally { window.CloseForGood(); }
    }));

    /// <summary>A model that answers each question with the next of its replies, whole, and records its requests.</summary>
    private sealed class ReplyingModel(params string[] replies) : IModelService
    {
        private int _asked;

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192));

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var reply = replies[Math.Min(_asked++, replies.Length - 1)];
            await Task.Yield();
            yield return AssistantResponseChunk.ForTextDelta(reply);
        }
    }
}
