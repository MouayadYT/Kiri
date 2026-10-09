using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Assistant.Tools;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The question the Assistant asks before it changes anything, as the user meets it (PROJECT_SPEC §4.8, step 115): inline in the answer it belongs to, with exactly what would be
/// done, a button that says yes that does not work for a moment, one that says no, and an answer that goes on only when the user says yes.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static readonly ToolDefinition SendNoteDefinition = ToolDefinition.Create(
        "send_note", "Sends a note.", [new ToolParameter("text", ToolParameterType.String, "The note.")], RiskLevel.SideEffect);

    private static ToolConfirmation NoteQuestion(string text = "hello there") =>
        new(
            ConfirmationKind.SendMessage,
            "Send this note to Omar?",
            [new ConfirmationDetail("To", "Omar"), new ConfirmationDetail("Note", text)],
            "Send",
            "It cannot be taken back.");

    private static ToolConfirmationRequested NoteRequest(Guid? conversation = null, string text = "hello there") =>
        new(SendNoteDefinition, new ToolCall("c1", "send_note", "{\"text\":\"" + text + "\"}"), conversation ?? Guid.NewGuid(), NoteQuestion(text));

    // ---- The panel ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheQuestionWaitsAndShowsWhatWouldBeDone_AndTheButtonThatSaysYesIsHeldBackForAMoment() => RunSta(() =>
    {
        var request = NoteRequest();
        var panel = new ToolConfirmationContent(request, TimeSpan.FromMilliseconds(300));

        Assert.Equal("Send this note to Omar?", panel.Title);
        Assert.Equal([("To", "Omar"), ("Note", "hello there")], panel.Details.Select(line => (line.Label, line.Value)));
        Assert.Equal("Send", panel.ApproveLabel);
        Assert.True(panel.HasWarning);
        Assert.True(panel.IsWide);
        Assert.True(panel.IsWaiting);

        // Pressed at once (a click or a key meant for something else), the button that says yes does nothing.
        Assert.False(panel.IsArmed);
        Assert.False(panel.CanApprove);
        Assert.False(panel.ApproveCommand.CanExecute(null));
        panel.ApproveCommand.Execute(null);
        panel.Approve();
        Assert.Equal(ToolConfirmationState.Pending, request.State);
        Assert.True(panel.DeclineCommand.CanExecute(null));

        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsArmed, "The button was never armed.");
        Assert.True(panel.ApproveCommand.CanExecute(null));
        panel.ApproveCommand.Execute(null);

        Assert.Equal(ToolConfirmationState.Approved, request.State);
        Assert.True(panel.IsAllowed && panel.IsFinished && !panel.IsWaiting);
        Assert.Equal("Allowed.", panel.ResultText);
        Assert.False(panel.ApproveCommand.CanExecute(null));
        Assert.False(panel.DeclineCommand.CanExecute(null));
        panel.Dispose();
    });

    [Fact]
    public void TheButtonThatSaysNoWorksAtOnce_AndNothingAnswersTheQuestionTwice() => RunSta(() =>
    {
        var request = NoteRequest();
        var panel = new ToolConfirmationContent(request, TimeSpan.FromMinutes(1));

        panel.DeclineCommand.Execute(null);
        panel.Approve();

        Assert.Equal(ToolConfirmationState.Declined, request.State);
        Assert.Equal("Not allowed. Nothing was done.", panel.ResultText);
        Assert.False(panel.IsAllowed);
        panel.Dispose();
    });

    [Fact]
    public void AQuestionThatEndsOnAnotherThread_IsFollowedByThePanel_AndCannotThenBeApproved() => RunSta(() =>
    {
        var request = NoteRequest();
        var panel = new ToolConfirmationContent(request, TimeSpan.Zero);
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // The time is up, on a thread of the broker's own.
        Task.Run(() => request.Expire());
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsFinished, "The panel did not follow the question.");

        Assert.Equal("No answer in time, so nothing was done.", panel.ResultText);
        Assert.Contains(nameof(ToolConfirmationContent.ResultText), changed);
        panel.Approve();
        Assert.Equal(ToolConfirmationState.Expired, request.State);
        Assert.False(panel.IsAllowed);
        panel.Dispose();
    });

    [Fact]
    public void AStoppedAnswerTakesTheQuestionBack_AndSaysSo() => RunSta(() =>
    {
        var request = NoteRequest();
        var panel = new ToolConfirmationContent(request, TimeSpan.Zero);

        request.Withdraw();

        Assert.Equal("Stopped before you answered. Nothing was done.", panel.ResultText);
        Assert.False(panel.CanApprove);
        panel.Dispose();
    });

    [Fact]
    public void ThePanelsTextIsNeverInALog() => RunSta(() =>
    {
        var panel = new ToolConfirmationContent(NoteRequest(text: "my secret words"), TimeSpan.Zero);

        Assert.Equal(nameof(ToolConfirmationContent), panel.ToString());
        Assert.Contains("my secret words", panel.Text, StringComparison.Ordinal);
        panel.Dispose();
    });

    // ---- How it is drawn ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePanelIsDrawnWithWhatWouldBeDoneAndTwoButtons_AndTheButtonsGiveWayToTheAnswer() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var request = NoteRequest(text: "I will be late.\nStart without me.");
        var panel = new ToolConfirmationContent(request, TimeSpan.Zero);
        var host = new ContentControl { Content = panel, Width = 520 };
        var window = new Window { Content = host, Width = 560, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            var texts = AllTextOf(host).ToList();
            Assert.Contains("Send this note to Omar?", texts);
            Assert.Contains("To", texts);
            Assert.Contains("Omar", texts);
            Assert.Contains("Note", texts);
            Assert.Contains("I will be late.\nStart without me.", texts);
            Assert.Contains("It cannot be taken back.", texts);
            var send = ButtonLabelled(host, "Send");
            var no = ButtonLabelled(host, "Don't allow");
            Assert.True(send.IsVisible && no.IsVisible && send.IsEnabled);
            Assert.Equal("Don't allow, and do nothing", AutomationPeer(no).GetName());
            RenderFixture(host, "tool-confirmation-waiting.png", 2);

            Click(no);
            Pump();

            Assert.False(ButtonsVisible(host, "Send") || ButtonsVisible(host, "Don't allow"));
            Assert.Contains(AllTextOf(host), text => text == "Not allowed. Nothing was done.");
            RenderFixture(host, "tool-confirmation-declined.png", 2);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.Close();
            panel.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    // "Always allow" (asked for after a timer was confirmed for the third time in a row): a third button on a question that may be answered so, which
    // says yes to this call and has the action not asked about again. A question about a message has no such button.
    [Fact]
    public void AQuestionAboutATimerOffersAlwaysAllow_AndOneAboutAMessageDoesNot() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var timer = ToolDefinition.Create("start_timer", "Starts a timer.", [], RiskLevel.SideEffect);
        var question = new ToolConfirmation(
            ConfirmationKind.ChangeSystem, "Start a timer of 10 minutes?", [new ConfirmationDetail("Length", "10 minutes")], "Start timer",
            "It is done in the Windows Clock app, which opens.");
        var request = new ToolConfirmationRequested(timer, new ToolCall("c1", "start_timer", """{"minutes":10}"""), Guid.NewGuid(), question, canAlwaysAllow: true);
        var panel = new ToolConfirmationContent(request, TimeSpan.Zero);
        var message = new ToolConfirmationContent(NoteRequest(), TimeSpan.Zero);
        var host = new ContentControl { Content = panel, Width = 520 };
        var other = new ContentControl { Content = message, Width = 520 };
        var window = new Window
        {
            Content = new StackPanel { Children = { host, other } }, Width = 560, Height = 900, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            Pump();

            // Three buttons, in this order; the message's question has the two it always had.
            var start = ButtonLabelled(host, "Start timer");
            var always = ButtonLabelled(host, "Always allow");
            var no = ButtonLabelled(host, "Don't allow");
            Assert.True(start.IsVisible && always.IsVisible && no.IsVisible && always.IsEnabled);
            Assert.True(start.TranslatePoint(default, host).X < always.TranslatePoint(default, host).X);
            Assert.True(always.TranslatePoint(default, host).X < no.TranslatePoint(default, host).X);
            Assert.True(no.TranslatePoint(new Point(no.ActualWidth, 0), host).X <= host.ActualWidth, "The three buttons do not fit the card.");
            Assert.Equal("Always allow: do it, and do not ask about this action again", AutomationPeer(always).GetName());
            Assert.True(ButtonsVisible(other, "Send") && !ButtonsVisible(other, "Always allow"));
            Assert.False(message.CanAlwaysAllow);
            Assert.False(message.AlwaysAllowCommand.CanExecute(null));
            RenderFixture(host, "tool-confirmation-always.png", 2);

            // Pressed on the message's panel by some other way, it answers nothing.
            message.ApproveAlways();
            Assert.True(message.IsWaiting);

            Click(always);
            Pump();

            Assert.Equal(ToolConfirmationState.Approved, request.State);
            Assert.True(request.IsAlways);
            Assert.Contains(AllTextOf(host), text => text == "Allowed, and not asked about again. You can change that in Settings, under Permissions.");
            Assert.False(ButtonsVisible(host, "Always allow"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.Close();
            panel.Dispose();
            message.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AButtonThatIsHeldBackLooksIt() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var panel = new ToolConfirmationContent(NoteRequest(), TimeSpan.FromMinutes(1));
        var host = new ContentControl { Content = panel, Width = 520 };
        var window = new Window { Content = host, Width = 560, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            var send = ButtonLabelled(host, "Send");
            Assert.False(send.IsEnabled);
            Assert.Equal(0.45, send.Opacity, 2);
            Assert.True(ButtonLabelled(host, "Don't allow").IsEnabled);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.Close();
            panel.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AnAllowedQuestionSaysSo_AndItsButtonsGoAway() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var panel = new ToolConfirmationContent(NoteRequest(), TimeSpan.Zero);
        var host = new ContentControl { Content = panel, Width = 520 };
        var window = new Window { Content = host, Width = 560, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            Click(ButtonLabelled(host, "Send"));
            Pump();

            Assert.Contains(AllTextOf(host), text => text == "Allowed.");
            Assert.False(ButtonsVisible(host, "Send") || ButtonsVisible(host, "Don't allow"));
            RenderFixture(host, "tool-confirmation-allowed.png", 2);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.Close();
            panel.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void TheQuestionIsDrawnInTheConversationAfterTheAssistantsWords() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var answer = new MessageViewModel(MessageRole.Assistant, "I can send that to Omar for you. Is this right?");
        var card = new ToolConfirmationContent(NoteRequest(text: "I'm running late. Start without me."), TimeSpan.Zero);
        answer.Content.Add(card);
        model.Messages.Add(new MessageViewModel(MessageRole.User, "Tell Omar I'm running late"));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            Assert.Contains(AllTextOf(panel), text => text == "Send this note to Omar?");
            Assert.True(ButtonLabelled(panel, "Send").IsVisible);
            RenderGlass(panel, "tool-confirmation-in-panel-2x.png", 2);
        }
        finally
        {
            card.Dispose();
            panel.Close();
        }
    }));

    // ---- The listener: the question goes in the answer it belongs to ---------------------------------------------------------

    private static AppEventBus NewEventBus() => new(NullLogger<AppEventBus>.Instance);

    [Fact]
    public void AQuestionOfTheConversationIsPutInItsAnswerAndMarkedShown_AndOneOfAnotherConversationIsLeftAlone() => RunSta(() =>
    {
        var bus = NewEventBus();
        var conversation = Guid.NewGuid();
        var shown = new List<MessageContent>();
        using var listener = new ToolConfirmationListener(bus, conversation, System.Windows.Threading.Dispatcher.CurrentDispatcher, shown.Add, TimeSpan.Zero);

        var ours = NoteRequest(conversation);
        var theirs = NoteRequest(Guid.NewGuid());
        var withoutAConversation = NoteRequest(Guid.Empty);
        foreach (var request in new[] { theirs, withoutAConversation, ours })
        {
            var publishing = bus.PublishAsync(request);
            WaitUntilFor(TimeSpan.FromSeconds(5), () => publishing.IsCompleted, "The question was not delivered.");
        }

        var panel = Assert.IsType<ToolConfirmationContent>(Assert.Single(shown));
        Assert.Equal("Send this note to Omar?", panel.Title);
        Assert.True(ours.IsShown);
        Assert.False(theirs.IsShown);
        Assert.False(withoutAConversation.IsShown);
    });

    [Fact]
    public void AQuestionThatIsAlreadyOverIsNotShown() => RunSta(() =>
    {
        var bus = NewEventBus();
        var conversation = Guid.NewGuid();
        var shown = new List<MessageContent>();
        using var listener = new ToolConfirmationListener(bus, conversation, System.Windows.Threading.Dispatcher.CurrentDispatcher, shown.Add, TimeSpan.Zero);
        var over = NoteRequest(conversation);
        over.Withdraw();

        var publishing = bus.PublishAsync(over);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => publishing.IsCompleted, "The question was not delivered.");

        Assert.Empty(shown);
        Assert.False(over.IsShown);
    });

    [Fact]
    public void WhenTheAnswerIsOverAQuestionStillWaitingIsWithdrawn_SoNoPanelCanApproveACallNoLongerBeingMade() => RunSta(() =>
    {
        var bus = NewEventBus();
        var conversation = Guid.NewGuid();
        var shown = new List<MessageContent>();
        var listener = new ToolConfirmationListener(bus, conversation, System.Windows.Threading.Dispatcher.CurrentDispatcher, shown.Add, TimeSpan.Zero);
        var request = NoteRequest(conversation);
        var publishing = bus.PublishAsync(request);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => publishing.IsCompleted, "The question was not delivered.");
        var panel = Assert.IsType<ToolConfirmationContent>(Assert.Single(shown));

        listener.Dispose();

        Assert.Equal(ToolConfirmationState.Withdrawn, request.State);
        Assert.Equal("Stopped before you answered. Nothing was done.", panel.ResultText);
        panel.Approve();
        Assert.False(panel.IsAllowed);

        // And a question that comes after is not shown at all.
        var late = NoteRequest(conversation);
        var latePublishing = bus.PublishAsync(late);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => latePublishing.IsCompleted, "The question was not delivered.");
        Assert.Single(shown);
        Assert.False(late.IsShown);
    });

    // ---- The whole of it: an answer whose tool changes something --------------------------------------------------------------

    // The real orchestrator, executor, confirmation and answer provider, to a model that asks to send a note and then says that it was sent.
    private static (ModelAnswerProvider Provider, Func<int> Runs, AppEventBus Bus) AskingProvider(bool withSurface = true)
    {
        var bus = NewEventBus();
        var broker = new ConfirmationBroker(bus, TimeProvider.System, NullLogger<ConfirmationBroker>.Instance, TimeSpan.FromSeconds(30));
        var runs = 0;
        var tool = new HandlerTool(
            SendNoteDefinition,
            (call, _, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, """{"sent":true}"""));
            },
            confirmation: arguments => NoteQuestion(arguments.GetProperty("text").GetString()!));
        var model = new RoundsModel(
            ToolsModelInfo,
            [ModelCalls("send_note", """{"text":"hello there"}""")],
            [AssistantResponseChunk.ForTextDelta("I sent it.")]);
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, toolRegistry: new ToolRegistry([tool]), toolExecutor: new ToolExecutor([tool], broker));
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), bus: withSurface ? bus : null);
        return (provider, () => Volatile.Read(ref runs), bus);
    }

    [Fact]
    public void AToolThatChangesSomethingWaitsInTheAnswerForTheUser_AndRunsOnlyWhenTheyAllowIt() => RunSta(() =>
    {
        var (provider, runs, _) = AskingProvider();
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "send a note to Omar", shown.Add, CancellationToken.None);
        WaitUntilFor(
            TimeSpan.FromSeconds(10),
            () => shown.Count == 1 && shown[0].Content.OfType<ToolConfirmationContent>().Any(),
            "The question did not appear in the answer.");

        // The answer is held at the question, and the tool has done nothing.
        var answer = shown[0];
        var panel = Assert.Single(answer.Content.OfType<ToolConfirmationContent>());
        Assert.Equal(MessageStatus.Answering, answer.Status);
        Assert.False(asked.IsCompleted);
        Assert.Equal(0, runs());
        Assert.Equal(("To", "Omar"), (panel.Details[0].Label, panel.Details[0].Value));

        WaitUntilFor(TimeSpan.FromSeconds(10), () => panel.IsArmed, "The button was never armed.");
        panel.ApproveCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => asked.IsCompleted, "The answer did not go on.");

        Assert.Equal(1, runs());
        Assert.True(panel.IsAllowed);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        Assert.Equal("I sent it.", answer.Content.OfType<TextContent>().Last().Text);
    });

    [Fact]
    public void ANoMeansTheToolDoesNothing_AndTheAnswerGoesOnWithWhatItIsTold() => RunSta(() =>
    {
        var (provider, runs, _) = AskingProvider();
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "send a note to Omar", shown.Add, CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => shown.Count == 1 && shown[0].Content.OfType<ToolConfirmationContent>().Any(), "No question.");
        var panel = shown[0].Content.OfType<ToolConfirmationContent>().Single();

        panel.DeclineCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => asked.IsCompleted, "The answer did not go on.");

        Assert.Equal(0, runs());
        Assert.False(panel.IsAllowed);
        Assert.Equal("Not allowed. Nothing was done.", panel.ResultText);
    });

    [Fact]
    public void StoppingTheAnswerWhileItWaitsTakesTheQuestionBack_AndTheToolNeverRuns() => RunSta(() =>
    {
        var (provider, runs, _) = AskingProvider();
        var shown = new List<MessageViewModel>();
        using var stop = new CancellationTokenSource();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "send a note to Omar", shown.Add, stop.Token);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => shown.Count == 1 && shown[0].Content.OfType<ToolConfirmationContent>().Any(), "No question.");
        var panel = shown[0].Content.OfType<ToolConfirmationContent>().Single();

        stop.Cancel();
        WaitUntilFor(TimeSpan.FromSeconds(10), () => asked.IsCompleted, "The answer did not stop.");

        Assert.Equal(0, runs());
        Assert.Equal(ToolConfirmationState.Withdrawn, panel.State);
        Assert.Equal(MessageStatus.Stopped, shown[0].Status);
        Assert.False(panel.CanApprove);
    });

    [Fact]
    public void WhereNothingShowsTheQuestionTheToolIsNotRun_AndTheAnswerSaysNoMoreThanTheModelSays() => RunSta(() =>
    {
        // An answer provider that is not listening: there is no one to ask, so nothing that changes something can run.
        var (provider, runs, _) = AskingProvider(withSurface: false);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "send a note to Omar", shown.Add, CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => asked.IsCompleted, "The answer did not end.");

        Assert.Equal(0, runs());
        Assert.Empty(shown.SelectMany(answer => answer.Content.OfType<ToolConfirmationContent>()));
    });

    // ---- demo confirm ------------------------------------------------------------------------------------------------------------

    private static DemoAnswerProvider DemoWithConfirmation()
    {
        var bus = NewEventBus();
        var broker = new ConfirmationBroker(bus, TimeProvider.System, NullLogger<ConfirmationBroker>.Instance, TimeSpan.FromSeconds(30));
        return new DemoAnswerProvider(
            new FakeClipboard(), TimeProvider.System, confirmationDemo: new ConfirmationDemo(bus, broker, TimeProvider.System));
    }

    [Fact]
    public void TheDemoAsksTheRealQuestionAboutAMadeUpPersonAndOnlyGoesOnWhenTheUserChooses() => RunSta(() =>
    {
        var demo = DemoWithConfirmation();
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(Guid.NewGuid(), "Demo confirm", shown.Add, CancellationToken.None);
        WaitUntilFor(
            TimeSpan.FromSeconds(10),
            () => shown.Count == 1 && shown[0].Content.OfType<ToolConfirmationContent>().Any(),
            "The question did not appear.");

        var answer = shown[0];
        var panel = answer.Content.OfType<ToolConfirmationContent>().Single();
        Assert.Equal(MessageStatus.Answering, answer.Status);
        Assert.False(asked.IsCompleted);
        Assert.Equal("Send this message to Omar?", panel.Title);
        Assert.Equal(["To", "Through", "Message"], panel.Details.Select(line => line.Label));
        Assert.Equal("Omar", panel.Details[0].Value);
        Assert.Contains("a sample: nothing is really sent", panel.Details[1].Value, StringComparison.Ordinal);
        Assert.Equal("I'm running late. Start without me.", panel.Details[2].Value);

        WaitUntilFor(TimeSpan.FromSeconds(10), () => panel.IsArmed, "The button was never armed.");
        panel.ApproveCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => asked.IsCompleted, "The demonstration did not end.");

        Assert.Equal(MessageStatus.Complete, answer.Status);
        Assert.Contains("You allowed it", answer.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing really left this PC", answer.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void TheDemoSaysNothingWasSentWhenTheUserChoosesNo_AndWhenTheyStopIt() => RunSta(() =>
    {
        var demo = DemoWithConfirmation();
        var declined = new List<MessageViewModel>();
        var no = demo.StreamAnswerAsync(Guid.NewGuid(), "demo confirm", declined.Add, CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => declined.Count == 1 && declined[0].Content.OfType<ToolConfirmationContent>().Any(), "No question.");

        declined[0].Content.OfType<ToolConfirmationContent>().Single().DeclineCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => no.IsCompleted, "The demonstration did not end.");
        Assert.Contains("You chose Don't allow, so nothing was sent.", declined[0].Text, StringComparison.Ordinal);

        using var stop = new CancellationTokenSource();
        var stopped = new List<MessageViewModel>();
        var stopping = demo.StreamAnswerAsync(Guid.NewGuid(), "demo confirm", stopped.Add, stop.Token);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => stopped.Count == 1 && stopped[0].Content.OfType<ToolConfirmationContent>().Any(), "No question.");

        stop.Cancel();
        WaitUntilFor(TimeSpan.FromSeconds(10), () => stopping.IsCompleted, "The demonstration did not stop.");

        Assert.Equal(MessageStatus.Stopped, stopped[0].Status);
        Assert.Equal(ToolConfirmationState.Withdrawn, stopped[0].Content.OfType<ToolConfirmationContent>().Single().State);
    });

    [Fact]
    public void TheDemoIsListed_AndWithoutItsPartsItSaysItIsNotThere() => RunSta(() =>
    {
        var listed = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo");
        var unavailable = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo confirm");

        Assert.Contains("demo confirm", listed!.Text, StringComparison.Ordinal);
        Assert.Equal("The confirmation test is not available here.", unavailable!.Text);
    });

    // ---- The app as it is wired --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRealAppAsksInTheConversation_AndAToolThatIsDeclinedChangesNothing() => RunSta(() =>
    {
        using var host = AppHost.Create();
        var conversation = Guid.NewGuid();
        var shown = new List<MessageContent>();
        using var listener = new ToolConfirmationListener(
            host.Services.GetRequiredService<IAppEventBus>(), conversation, System.Windows.Threading.Dispatcher.CurrentDispatcher, shown.Add, TimeSpan.Zero);

        // The app's own executor and confirmation, asked to change the sound; nothing is changed unless the user says yes, and here they say no.
        var running = host.Services.GetRequiredService<IToolExecutor>()
            .ExecuteAsync(new ToolCall("c1", "set_volume", """{"percent":10}"""), new ToolContext(conversation, "set the volume to 10"), CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => shown.Count == 1, "The app did not ask.");

        var panel = Assert.IsType<ToolConfirmationContent>(shown[0]);
        Assert.Equal("Set the volume to 10%?", panel.Title);
        Assert.False(running.IsCompleted);
        panel.DeclineCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => running.IsCompleted, "The call did not end.");

        Assert.Equal(ToolResultStatus.Declined, running.Result.Status);
        Assert.IsType<ConfirmationBroker>(host.Services.GetRequiredService<IPermissionService>());
        Assert.IsType<ConfirmationDemo>(host.Services.GetRequiredService<IConfirmationDemo>());
        Assert.Contains("demo confirm", host.Services.GetRequiredService<IAnswerProvider>().Answer("demo")!.Text, StringComparison.Ordinal);
    });
}
