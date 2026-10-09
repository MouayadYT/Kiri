using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.Core.Ipc;
using Assistant.Core.Voice;
using Assistant.UI.Controls;
using Assistant.UI.Explorer;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The Assistant's full window (PROJECT_SPEC §4.3): opening the app shows it, whether or not the app is already running; its composer's plus and
/// microphone and the button that starts a new conversation all work; and closing it leaves the Assistant running.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static void Press(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    [Fact]
    public void ARequestToShowTheFullWindowWaitsForTheWindowsAndThenIsShownEachTime()
    {
        var requests = new FullViewRequests();
        var shown = 0;

        // The app was opened again before its windows were made: the request is kept, once.
        requests.Post();
        requests.Post();
        Assert.Equal(0, shown);

        requests.Connect(() => shown++, action => action());
        Assert.Equal(1, shown);

        requests.Post();
        requests.Post();
        Assert.Equal(3, shown);
    }

    [Fact]
    public async Task TheAppsPipeTakesTheRequestToShowTheFullWindow_AndAnAppWithoutOneAnswersThatItDoesNotKnowIt()
    {
        var requests = new FullViewRequests();
        var shown = new System.Collections.Concurrent.BlockingCollection<int>();
        requests.Connect(() => shown.Add(1), action => action());
        var pipe = LocalPipe.CreateUniqueName("Assistant.Tests.FullView");
        using var integration = new ExplorerIntegration(
            new ExplorerIntegrationOptions(pipe), new ExplorerFileRequests(), new FakeExplorerMenu(), new InMemorySettingsService(),
            NullLogger<InvocationServer>.Instance, NullLogger<ExplorerIntegration>.Instance, fullView: requests);

        await integration.StartAsync(CancellationToken.None);
        try
        {
            // What a second start of the app does: it asks the one that runs, which shows its window, and is told it was taken.
            var taken = false;
            for (var attempt = 0; attempt < 100 && !taken; attempt++)
            {
                taken = await Task.Run(() => Assistant.Windows.Startup.RunningAssistant.TryShowFullView(pipe));
            }

            Assert.True(taken, "The running app did not take the request.");
            Assert.True(shown.TryTake(out _, TimeSpan.FromSeconds(5)), "The full window was not asked for.");
        }
        finally
        {
            await integration.StopAsync(CancellationToken.None);
        }

        using var older = new ExplorerIntegration(
            new ExplorerIntegrationOptions(pipe), new ExplorerFileRequests(), new FakeExplorerMenu(), new InMemorySettingsService(),
            NullLogger<InvocationServer>.Instance, NullLogger<ExplorerIntegration>.Instance);
        Assert.Equal(InvocationErrorCode.UnknownAction, older.Handle(InvocationRequest.ShowFullView).Error);
    }

    [Fact]
    public void ANewConversationOpensEmptyInTheWorkspace_TakesItsFirstMessage_AndIsForgottenWhenLeftEmpty() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(model));
        var earlier = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi"), new MessageViewModel(MessageRole.Assistant, "Hello.")], ReferenceNow);
        var started = 0;
        history.NewConversationStarted += (_, _) => started++;
        history.Draft = "left over";
        Assert.True(history.NewConversationCommand.CanExecute(null));

        history.NewConversationCommand.Execute(null);

        // Empty, first in the list, open and ready to be typed in.
        var fresh = history.Selected!;
        Assert.NotSame(earlier, fresh);
        Assert.Same(fresh, history.Conversations[0]);
        Assert.Empty(fresh.Messages);
        Assert.True(fresh.IsLoaded);
        Assert.Equal(HistoryConversationViewModel.NewConversationTitle, fresh.Title);
        Assert.Equal("", history.Draft);
        Assert.Equal(1, started);

        // Pressed again with nothing asked yet, it is the same conversation, not another empty one.
        history.NewConversationCommand.Execute(null);
        Assert.Same(fresh, history.Selected);
        Assert.Equal(2, history.Conversations.Count);

        // Left without a message, it was never a conversation.
        history.Selected = earlier;
        Assert.Equal([earlier], history.Conversations);

        // One that is asked something is a conversation like any other, and stays.
        history.NewConversationCommand.Execute(null);
        var asked = history.Selected!;
        Assert.True(history.Send("What is new?"));
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        Assert.Equal(["What is new?"], model.Requests[0].Messages.Select(message => message.Text));
        Assert.Equal("What is new?", asked.Title);

        // While its answer comes, another cannot be started.
        Assert.False(history.NewConversationCommand.CanExecute(null));
        history.Stop();
        WaitUntil(() => !history.IsAnswering, "The answer did not stop.");
        history.Selected = earlier;
        Assert.Contains(asked, history.Conversations);
    });

    [Fact]
    public void WhatIsSaidToTheFullWindowsMicrophoneIsSentWhenTheSpeakerStops() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(model), voice: voice);
        var card = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi"), new MessageViewModel(MessageRole.Assistant, "Hello.")], ReferenceNow);

        voice.Start();
        var listening = Assert.Single(input.Listenings);
        listening.Raise("tell me more", null);
        Pump();
        Assert.Equal("Tell me more", voice.Transcript);
        listening.Raise("tell me more", SpeechEndReason.Endpoint);
        Pump();

        WaitUntil(() => model.Requests.Count == 1, "What was said was not asked.");
        Assert.Equal("Tell me more", card.Messages[2].Text);
        Assert.False(voice.IsListening);
        history.Stop();
        WaitUntil(() => !history.IsAnswering, "The answer did not stop.");

        // Said while it cannot be sent (no conversation is open), it waits in the composer.
        var empty = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(new ScriptedModel()), voice: voice);
        voice.Start();
        input.Listenings[^1].Raise("and this", SpeechEndReason.Endpoint);
        Pump();
        Assert.Equal("And this", empty.Draft);
    });

    [Fact]
    public void TheFullWindowsPlusMicrophoneAndNewConversationButtonsWork() => RunSta(() => WithTheme(() =>
    {
        var model = new ScriptedModel();
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var history = new HistoryViewModel(new FixedClock(ReferenceNow), null, LocalAnswers(model), voice: voice);
        var earlier = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Hi"), new MessageViewModel(MessageRole.Assistant, "Hello.")], ReferenceNow);
        var window = new HistoryWindow(history, new FakeFrameFactory(), new FakePlacement(), new MenuBackdrops())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        try
        {
            window.Show();
            Pump();
            var plus = Named<Button>(window, "AttachButton");
            var microphone = Named<Button>(window, "ComposerVoiceButton");
            var compose = Named<Button>(window, "ComposeButton");

            // None of the three is a picture of a button any more.
            Assert.True(plus.IsEnabled);
            Assert.True(microphone.IsEnabled);
            Assert.True(compose.IsEnabled);

            // The plus opens its menu of Photos and Files.
            Press(plus);
            Pump();
            var menu = window.PlusMenu;
            Assert.True(menu.IsOpen);
            Assert.Equal(["Photos", "Files"], menu.Items.OfType<MenuItem>().Select(item => item.Header as string));
            menu.IsOpen = false;
            Pump();

            // The microphone listens when it is pressed, is lit and says so; pressed again it tells the recognizer the speaker is done, and once the
            // recognizer has ended (here with nothing heard) it is the microphone again.
            Press(microphone);
            Pump();
            Assert.True(voice.IsListening);
            Assert.Equal("Stop listening", AutomationProperties.GetName(microphone));

            // While it listens the composer knows: words that a recognizer hands over by pasting them (Handy) are taken there as what was said.
            Assert.True(Named<PromptInputControl>(window, "ComposerInput").IsMicrophoneActive);
            Press(microphone);
            Pump();
            Assert.True(input.Listenings[0].FinishCalled);
            input.Listenings[0].Raise("", SpeechEndReason.Endpoint);
            Pump();
            Assert.False(voice.IsListening);
            Assert.Equal("Use microphone", AutomationProperties.GetName(microphone));
            Assert.False(Named<PromptInputControl>(window, "ComposerInput").IsMicrophoneActive);

            // The button over the list starts a new conversation: the workspace is empty, and its composer is there to type in.
            Press(compose);
            Pump();
            Assert.NotSame(earlier, history.Selected);
            Assert.Empty(history.Selected!.Messages);
            Assert.True(Named<PromptInputControl>(window, "ComposerInput").IsVisible);

            // Hiding the window, which is what closing it does, lets go of the microphone.
            Press(microphone);
            Pump();
            Assert.True(voice.IsListening);
            window.Hide();
            Pump();
            Assert.False(voice.IsListening);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void ClosingTheFullWindowHidesItAndLeavesTheAssistantRunning() => RunSta(() => WithTheme(() =>
    {
        var history = new HistoryViewModel(new FixedClock(ReferenceNow));
        var window = new HistoryWindow(history, new FakeFrameFactory(), new FakePlacement(), new MenuBackdrops())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Pump();

            window.Close();
            Pump();

            // Hidden, not gone: it comes back as it was when the app is opened again.
            Assert.False(window.IsVisible);
            Assert.False(closed);
            window.ShowAndActivate();
            Pump();
            Assert.True(window.IsVisible);
        }
        finally { window.CloseForGood(); }
    }));
}
