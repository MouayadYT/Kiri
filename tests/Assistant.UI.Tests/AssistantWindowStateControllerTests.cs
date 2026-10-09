using Assistant.Core.Domain;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static (AssistantWindowStateController Controller, FakeAssistantWindow Window, SearchOrAskViewModel Bar,
        ConversationViewModel Conversation, FakeMicrophone BarMicrophone, FakeMicrophone ConversationMicrophone) CreateController()
    {
        var barMicrophone = new FakeMicrophone();
        var conversationMicrophone = new FakeMicrophone();
        var bar = CreateBarModel(barMicrophone);
        var conversation = CreateConversationModel(conversationMicrophone);
        var window = new FakeAssistantWindow();
        return (new AssistantWindowStateController(window, bar, conversation), window, bar, conversation,
            barMicrophone, conversationMicrophone);
    }

    [Fact]
    public void TheHotkeyDiscardsDraggedPositionAndInvokesAtThePointer()
    {
        var (controller, window, _, _, _, _) = CreateController();
        Assert.Equal(AssistantWindowState.Compact, controller.State);

        controller.Invoke();
        Assert.Equal([null], window.Shown);

        // Each hotkey invocation follows the pointer, including when the window was dragged.
        var dropped = new ScreenPoint(-700, 300);
        window.SurfaceTop = dropped;
        window.RaiseMoved();
        Assert.Equal(dropped, controller.DraggedTo);
        controller.Invoke();
        Assert.Null(window.Shown[^1]);
        Assert.Null(controller.DraggedTo);

        // Once the window is hidden, it is forgotten.
        window.RaiseHidden();
        Assert.Null(controller.DraggedTo);
        controller.Invoke();
        Assert.Null(window.Shown[^1]);
    }

    [Fact]
    public async Task TheShortcutThatPutsTheAssistantAwayStopsTheQuestionItIsWorkingOn()
    {
        var stopped = new TaskCompletionSource();
        var answers = new ScriptedAnswers
        {
            Respond = async (_, _, _, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    stopped.TrySetResult();
                    throw;
                }
            },
        };
        var conversation = CreateConversationModel(answers: answers);
        var window = new FakeAssistantWindow();
        var controller = new AssistantWindowStateController(window, CreateBarModel(new FakeMicrophone()), conversation);

        conversation.StartNew("what is on my calendar?");
        Assert.True(conversation.IsAnswering);

        window.IsShowing = true;
        controller.Toggle();

        Assert.Equal(1, window.Dismissals);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheShortcutOpensTheAssistantAndPressedAgainPutsItAwayKeepingWhatWasTyped()
    {
        var (controller, window, bar, _, _, _) = CreateController();

        // Away: the shortcut opens it, as Invoke does.
        controller.Toggle();
        Assert.Equal([null], window.Shown);
        Assert.Equal(0, window.Dismissals);

        // Up: the same shortcut puts it away, and opens nothing. What was typed stays for the next time.
        window.IsShowing = true;
        bar.Query = "9+10";
        controller.Toggle();
        Assert.Equal(1, window.Dismissals);
        Assert.Single(window.Shown);
        Assert.Equal("9+10", bar.Query);

        // On its way out, or gone: the shortcut brings it back.
        window.IsShowing = false;
        window.RaiseHidden();
        controller.Toggle();
        Assert.Equal(2, window.Shown.Count);
        Assert.Equal(1, window.Dismissals);
        Assert.Equal("9+10", bar.Query);

        // The conversation is put away by it too, whatever the window shows.
        window.IsShowing = true;
        window.State = AssistantWindowState.FloatingConversation;
        controller.Toggle();
        Assert.Equal(2, window.Dismissals);
    }

    [Fact]
    public void AskingStartsTheConversationWithTheQuestionAsTypedAndGrowsTheWindowOnce()
    {
        var (controller, window, bar, conversation, _, _) = CreateController();
        var states = new List<AssistantWindowState>();
        controller.StateChanged += (_, _) => states.Add(controller.State);
        bar.Query = "  What is on my calendar?  ";

        Assert.True(bar.AskCommand.CanExecute(null));
        bar.AskCommand.Execute(null);

        Assert.Equal(1, window.Expansions);
        Assert.Equal(AssistantWindowState.FloatingConversation, controller.State);
        Assert.Equal([AssistantWindowState.FloatingConversation], states);
        var message = Assert.Single(conversation.Messages);
        Assert.Equal((MessageRole.User, "  What is on my calendar?  "), (message.Role, message.Text));

        // The bar is already on its way to being the panel, so another Enter asks nothing more.
        bar.AskCommand.Execute(null);
        Assert.Equal(1, window.Expansions);
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void TheTypedQuestionStaysInTheBarUntilItHasGrownAndThenGoes()
    {
        var (_, window, bar, _, _, _) = CreateController();
        bar.Query = "Synthetic question";

        bar.AskCommand.Execute(null);

        // Still there, on screen, while the pill grows: an empty placeholder would flash under the fading text.
        Assert.Equal("Synthetic question", bar.Query);
        Assert.False(bar.IsPlaceholderVisible);

        window.RaiseExpanded();
        Assert.Equal("", bar.Query);

        // Only an asked question is cleared: a new draft typed later stays.
        bar.Query = "Next draft";
        window.RaiseExpanded();
        window.RaiseHidden();
        Assert.Equal("Next draft", bar.Query);
    }

    [Fact]
    public void DismissingWhileItGrowsStillClearsTheAskedQuestionButNotAnUnaskedDraft()
    {
        var (_, window, bar, _, _, _) = CreateController();

        // A draft that was never asked is kept when the bar is dismissed (PROJECT_SPEC §4.1).
        bar.Query = "Unfinished thought";
        window.RaiseHidden();
        Assert.Equal("Unfinished thought", bar.Query);

        bar.AskCommand.Execute(null);
        window.RaiseHidden();
        Assert.Equal("", bar.Query);
    }

    [Fact]
    public void VoiceInputTheUserHadOnCarriesOnInTheConversation()
    {
        var (_, window, bar, conversation, barMicrophone, conversationMicrophone) = CreateController();
        bar.Voice.Start();
        bar.Query = "Synthetic question";

        bar.AskCommand.Execute(null);

        Assert.False(bar.Voice.IsListening);
        Assert.True(Assert.Single(barMicrophone.Sessions).IsDisposed);
        Assert.True(conversation.Voice.IsListening);
        Assert.Single(conversationMicrophone.Sessions);
        Assert.Equal(1, window.Expansions);

        // Without it, nothing listens.
        window.RaiseHidden();
        conversation.Voice.Stop();
        window.State = AssistantWindowState.Compact;
        bar.Query = "Another question";
        bar.AskCommand.Execute(null);
        Assert.False(conversation.Voice.IsListening);
        Assert.Single(conversationMicrophone.Sessions);
    }

    private sealed class FakeAssistantWindow : IAssistantWindow
    {
        public AssistantWindowState State { get; set; }
        public ScreenPoint? SurfaceTop { get; set; }
        public bool IsShowing { get; set; }
        public List<ScreenPoint?> Shown { get; } = [];
        public int Expansions { get; private set; }

        public event EventHandler? Moved;
        public event EventHandler? Expanded;
        public event EventHandler? Hidden;
        public event EventHandler? AssistantStateChanged;

        public void ShowAndFocus(ScreenPoint? surfaceTop) => Shown.Add(surfaceTop);

        public List<ScreenPoint?> ConversationsShown { get; } = [];

        public List<NearWindowTarget> ShownNear { get; } = [];

        public void ShowConversationNear(NearWindowTarget target)
        {
            ShownNear.Add(target);
            State = AssistantWindowState.FloatingConversation;
            AssistantStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ShowConversation(ScreenPoint? surfaceTop)
        {
            ConversationsShown.Add(surfaceTop);
            State = AssistantWindowState.FloatingConversation;
            AssistantStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ExpandToConversation()
        {
            Expansions++;
            State = AssistantWindowState.FloatingConversation;
            AssistantStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public int ForegroundTaken { get; private set; }

        public bool TakeForeground()
        {
            ForegroundTaken++;
            return true;
        }

        public int Dismissals { get; private set; }

        public void Dismiss() => Dismissals++;

        public int HiddenAtOnce { get; private set; }

        public bool HideNow()
        {
            HiddenAtOnce++;
            return true;
        }

        public void RaiseMoved() => Moved?.Invoke(this, EventArgs.Empty);
        public void RaiseExpanded() => Expanded?.Invoke(this, EventArgs.Empty);
        public void RaiseHidden() => Hidden?.Invoke(this, EventArgs.Empty);
    }
}
