using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Home;
using Assistant.Core.Tools;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

// What the Assistant did, shown as the thing itself (the home and message references): a device of the home as its row, alone on the glass as a pill
// when it is the whole answer, and a message as the person, the service and the bubble.
public sealed partial class PromptInputControlTests
{
    private sealed class SwitchingHome : IHomeAssistant
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsConnected => true;

        public string? Address => "http://192.168.1.20:8123";

        public IReadOnlyList<HomeDevice> Known => [];

        public HomeFailure Failure { get; set; }

        public string? StateAfter { get; set; }

        public List<string> Calls { get; } = [];

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TakeOverEarlierAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<HomeStatus> ConnectAsync(string address, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HomeStatus> CheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HomeDevices> GetDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HomeCallResult> CallAsync(
            string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"{domain}.{service} {deviceId}");
            return Task.FromResult(new HomeCallResult(Failure, Failure == HomeFailure.None ? StateAfter : null));
        }
    }

    private sealed class FixedRun(params string[] tools) : IAgentTaskView
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public AgentTaskSnapshot Snapshot { get; } = new()
        {
            Id = Guid.NewGuid(),
            StartedAt = Now,
            EndedAt = Now.AddSeconds(2),
            Status = AgentTaskStatus.Completed,
            Steps =
            [
                .. tools.Select((tool, index) => new AuditEntry
                {
                    Id = Guid.NewGuid(), Kind = AuditKind.ToolCall, Name = tool, Summary = AuditText.ToolPhrase(tool), Sequence = index + 1,
                    StartedAt = Now, EndedAt = Now.AddMilliseconds(400), Status = AuditStatus.Succeeded,
                }),
            ],
        };

        public bool Cancel() => false;
    }

    private static HomeDeviceContent FanCard(IHomeAssistant? home = null, IAgentTaskView? run = null, string state = "off") =>
        new(new HomeControlled("fan.window_fan", "Window Fan", "fan", state), home, run);

    // ---- the device's card ----

    [Fact]
    public void TheCardSaysWhatTheDeviceIsNow_AndItsGlyphSwitchesIt() => RunSta(() =>
    {
        var home = new SwitchingHome { StateAfter = "on" };
        var card = FanCard(home);

        Assert.Equal(("Window Fan", "Off", false, true), (card.Name, card.StateText, card.IsOn, card.CanToggle));
        Assert.Equal("Turn on Window Fan", card.ToggleName);
        Assert.Equal("Window Fan, Off", card.Text);

        card.ToggleCommand.Execute(null);
        Pump();

        Assert.Equal(["homeassistant.toggle fan.window_fan"], home.Calls);
        Assert.Equal(("On", true, false, "Turn off Window Fan"), (card.StateText, card.IsOn, card.IsBusy, card.ToggleName));
    });

    [Fact]
    public void WhenHomeAssistantDoesNotTakeThePressTheCardSaysSo_AndKeepsWhatTheDeviceWas() => RunSta(() =>
    {
        var home = new SwitchingHome { Failure = HomeFailure.Unreachable };
        var card = FanCard(home, state: "on");

        card.ToggleCommand.Execute(null);
        Pump();

        Assert.Equal(("On", true, "Home Assistant did not answer."), (card.StateText, card.IsOn, card.Problem));
        Assert.Contains("Home Assistant did not answer.", card.Text, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData("lock.front_door", "lock", "locked", "lock.unlock lock.front_door")]
    [InlineData("lock.front_door", "lock", "unlocked", "lock.lock lock.front_door")]
    [InlineData("cover.blinds", "cover", "closed", "cover.open_cover cover.blinds")]
    [InlineData("climate.bedroom", "climate", "cool", "homeassistant.toggle climate.bedroom")]
    public void EachKindOfDeviceIsSwitchedItsOwnWay(string id, string kind, string state, string call) => RunSta(() =>
    {
        var home = new SwitchingHome();
        var card = new HomeDeviceContent(new HomeControlled(id, "It", kind, state), home);

        card.ToggleCommand.Execute(null);
        Pump();

        Assert.Equal([call], home.Calls);
    });

    [Fact]
    public void WithoutAHomeAssistant_OrForASceneTheGlyphOnlyShows() => RunSta(() =>
    {
        Assert.False(FanCard().CanToggle);
        Assert.False(FanCard().ToggleCommand.CanExecute(null));
        Assert.False(new HomeDeviceContent(new HomeControlled("scene.movie_night", "Movie Night", "scene", "scening"), new SwitchingHome()).CanToggle);
    });

    [Fact]
    public void PressingTheCardOpensWhatWasDoneStepByStep_AndPressingItAgainClosesIt() => RunSta(() =>
    {
        var card = FanCard(run: new FixedRun("get_home_devices", "control_home_device"));

        Assert.True(card.HasSteps);
        Assert.False(card.ShowsSteps);
        Assert.Equal(["Look at your home devices", "Control a home device"], card.Steps.Select(step => step.Summary));

        card.OpenCommand.Execute(null);
        Assert.True(card.ShowsSteps);
        card.OpenCommand.Execute(null);
        Assert.False(card.ShowsSteps);

        // Shown alone, a press asks whoever shows it to open the conversation, with the steps open in it.
        var asked = 0;
        card.OpenRequested += (_, _) => asked++;
        card.OpenCommand.Execute(null);
        Assert.Equal((1, true), (asked, card.IsExpanded));
    });

    // ---- the pill ----

    private static (MessageViewModel Answer, HomeDeviceContent Card) DeviceAnswer(IHomeAssistant? home = null)
    {
        var card = FanCard(home, new FixedRun("control_home_device"));
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        answer.Content.Add(card);
        return (answer, card);
    }

    // Asks a question from the bar and runs the fold, so that the window waits as the Working pill.
    private static FakeFrames AskAndWait(AssistantSetup assistant, List<FakeFrames> frames)
    {
        assistant.Controller.Invoke();
        Advance(frames[0], 300);
        var input = Named<PromptInputControl>(assistant.Window, "PromptInput");
        input.Text = "turn off the fan";
        Assert.True(input.TrySubmit());
        var morph = frames[1];
        morph.Tick(T0);
        Settle(morph);
        Assert.True(assistant.Window.IsWaitingForAnswer);
        return morph;
    }

    private static void Settle(FakeFrames morph)
    {
        for (var steps = 0; morph.Running && steps < 2000; steps++)
        {
            morph.Tick(morph.Last + TimeSpan.FromMilliseconds(5));
        }
    }

    [Fact]
    public void AnAnswerThatIsADeviceOpensAsItsPill_AndAPressOnItOpensTheConversationWithTheSteps() => RunSta(() => WithTheme(() =>
    {
        Appear.Enabled = false;
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(new FakePlacement(), answers, frames);
        var (window, _, conversation) = assistant;
        var expanded = 0;
        window.Expanded += (_, _) => expanded++;
        try
        {
            var morph = AskAndWait(assistant, frames);
            var (answer, card) = DeviceAnswer(new SwitchingHome { StateAfter = "on" });

            answers.Show(answer);

            // The Working pill opens into the result's pill, not into the panel.
            Assert.True(window.IsShowingResult);
            Assert.False(window.IsWaitingForAnswer);
            Assert.False(window.IsExpanding);
            Settle(morph);
            var pill = GlassRegion(window);
            Assert.Equal((418, 73), (pill.Width, pill.Height));
            Assert.Equal(SurfaceForm.Pill(418, 73), window.Form);
            Assert.Equal(305, pill.Left + (pill.Width / 2), 3);
            Assert.Equal((Visibility.Visible, 1.0, true), (GridNamed(window, "ResultLayer").Visibility, GridNamed(window, "ResultLayer").Opacity, GridNamed(window, "ResultLayer").IsHitTestVisible));
            Assert.Equal(Visibility.Collapsed, GridNamed(window, "WorkingLayer").Visibility);
            Assert.Equal(0, GridNamed(window, "ConversationLayer").Opacity);
            Assert.False(GridNamed(window, "ConversationLayer").IsHitTestVisible);
            Assert.Equal(0, expanded);
            var texts = AllTextOf(GridNamed(window, "ResultLayer")).ToList();
            Assert.Contains("Window Fan", texts);
            Assert.Contains("Off", texts);
            RenderGlass(window, "result-pill-device.png", 2);

            // Words the model says afterwards do not open anything: the pill is the answer.
            answer.Content.Add(new TextContent("The fan is off."));
            Assert.True(window.IsShowingResult);

            // The glyph switches the device where it is, and the pill stays.
            var toggle = Descendants<Button>(GridNamed(window, "ResultLayer")).Single(button => (string)button.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) == "Turn on Window Fan");
            Click(toggle);
            Pump();
            Assert.Equal("On", card.StateText);
            Assert.True(window.IsShowingResult);
            RenderGlass(window, "result-pill-device-on.png", 2);

            // A press anywhere else opens the panel around it, with what was done open under the device.
            card.OpenCommand.Execute(null);
            Assert.False(window.IsShowingResult);
            Assert.True(window.IsExpanding);
            Settle(morph);
            var panel = GlassRegion(window);
            Assert.Equal((418, 598), (panel.Width, panel.Height));
            Assert.Equal(1, GridNamed(window, "ConversationLayer").Opacity);
            Assert.True(GridNamed(window, "ConversationLayer").IsHitTestVisible);
            Assert.Equal(Visibility.Collapsed, GridNamed(window, "ResultLayer").Visibility);
            Assert.True(card.ShowsSteps);
            Assert.Equal(1, expanded);
            Assert.Equal(2, conversation.Messages.Count);
            Pump();
            RenderGlass(window, "result-pill-opened.png", 2);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
        }
    }));

    [Fact]
    public void AnAnswerWithMoreThanTheDeviceOpensThePanel_AndSoDoesMoreThatComesWhileThePillShows() => RunSta(() => WithTheme(() =>
    {
        Appear.Enabled = false;
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(new FakePlacement(), answers, frames);
        var window = assistant.Window;
        try
        {
            var morph = AskAndWait(assistant, frames);
            var (answer, _) = DeviceAnswer();
            answers.Show(answer);
            Settle(morph);
            Assert.True(window.IsShowingResult);

            // Something that is neither the device nor words (a sum, a question to answer) needs the panel.
            answer.Content.Add(new CalculationResult("2 + 2", "4"));

            Assert.False(window.IsShowingResult);
            Assert.True(window.IsExpanding);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
        }

        frames = [];
        answers = new StreamingAnswers { Hold = true };
        assistant = CreateAssistant(new FakePlacement(), answers, frames);
        window = assistant.Window;
        try
        {
            AskAndWait(assistant, frames);
            var words = new MessageViewModel(MessageRole.Assistant, "Which fan do you mean?") { Status = MessageStatus.Answering };
            answers.Show(words);

            Assert.False(window.IsShowingResult);
            Assert.True(window.IsExpanding);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void EscPutsTheResultsPillAway() => RunSta(() => WithTheme(() =>
    {
        Appear.Enabled = false;
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(new FakePlacement(), answers, frames);
        var window = assistant.Window;
        try
        {
            var morph = AskAndWait(assistant, frames);
            answers.Show(DeviceAnswer().Answer);
            Settle(morph);
            Assert.True(window.IsShowingResult);

            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Advance(frames[0], 400);

            Assert.False(window.IsShowingResult);
            Assert.False(window.IsShowing);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
        }
    }));

    // ---- a message ----

    private static ToolConfirmationRequested MessageRequest(string through = "iMessage chat with Sami in Beeper", string text = "On my way") =>
        new(
            ToolDefinition.Create("send_message", "Sends.", [], RiskLevel.SideEffect),
            new ToolCall("c1", "send_message", "{}"),
            Guid.NewGuid(),
            new ToolConfirmation(
                ConfirmationKind.SendMessage, "Send this message to Sami?",
                [new ConfirmationDetail("To", "Sami"), new ConfirmationDetail("Through", through), new ConfirmationDetail("Message", text)],
                "Send", "A message cannot be taken back once it is sent."));

    [Theory]
    [InlineData("iMessage chat with Sami in Beeper", "iMessage", "iMessage")]
    [InlineData("Beeper chat with Sami in Beeper", "Beeper", "Beeper")]
    [InlineData("Chat with Sami in Beeper", "Beeper", "Beeper")]
    [InlineData("WhatsApp chat with Sami in Beeper", "WhatsApp", "WhatsApp")]
    [InlineData("Threema chat with Sami in Some App", "", "Threema chat with Sami in Some App")]
    public void TheQuestionAboutAMessageSaysWhoItIsForAndWhichServiceItGoesThrough(string through, string service, string channel) => RunSta(() =>
    {
        using var panel = new ToolConfirmationContent(MessageRequest(through), TimeSpan.Zero);

        Assert.True(panel.IsMessage);
        Assert.False(panel.IsGeneral);
        Assert.Equal(("Sami", "S", "On my way", service, channel), (panel.MessageTo, panel.MessageInitials, panel.MessageText, panel.MessageService, panel.MessageChannel));

        // The mark is the service's own: iMessage and Beeper are told apart by it.
        Assert.Same(MessageChannelMark.Glyph(service), panel.MessageMarkGlyph);
        Assert.Equal(MessageChannelMark.Background(service).ToString(), panel.MessageMarkBackground.ToString());
    });

    [Fact]
    public void TheMarksOfIMessageAndBeeperAreNotTheSame()
    {
        Assert.NotSame(MessageChannelMark.Glyph("iMessage"), MessageChannelMark.Glyph("Beeper"));
        var imessage = Assert.IsType<LinearGradientBrush>(MessageChannelMark.Background("iMessage"));
        var beeper = Assert.IsType<LinearGradientBrush>(MessageChannelMark.Background("Beeper"));
        Assert.NotEqual(imessage.GradientStops[0].Color, beeper.GradientStops[0].Color);
        Assert.Equal(("SK", "S", "?"), (MessageChannelMark.Initials("Sami Khan"), MessageChannelMark.Initials(" sami "), MessageChannelMark.Initials("")));
    }

    [Fact]
    public void AQuestionThatIsNotTheMessagingToolsIsDrawnLineByLineAsBefore() => RunSta(() =>
    {
        using var note = new ToolConfirmationContent(NoteRequest(), TimeSpan.Zero);

        Assert.False(note.IsMessage);
        Assert.True(note.IsGeneral);
        Assert.False(note.IsGone);
    });

    [Fact]
    public void TheMessageIsAskedAboutAsTheMessageItself_AndGoesOnceTheUserSaysSend() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var request = MessageRequest(text: "On my way.\nSee you at eight.");
        var panel = new ToolConfirmationContent(request, TimeSpan.Zero);
        var host = new ContentControl { Content = panel, Width = 386 };
        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Padding = new Thickness(16), Child = host },
            Width = 440, Height = 520, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            Pump();

            var texts = AllTextOf(host).ToList();
            Assert.Contains("Ready to send it?", texts);
            Assert.Contains("Sami", texts);
            Assert.Contains("iMessage", texts);
            Assert.Contains("On my way.\nSee you at eight.", texts);
            Assert.DoesNotContain("Send this message to Sami?", texts.Where(text => IsShown(host, text)));
            // (The line-by-line form of the question is in the same template, put away.)
            var send = Descendants<Button>(host).Single(button => button.IsVisible && button.Content as string == "Send");
            var cancel = Descendants<Button>(host).Single(button => button.IsVisible && button.Content as string == "Cancel");
            Assert.True(send.IsEnabled);
            Assert.False(ButtonsVisible(host, "Don't allow") || ButtonsVisible(host, "Always allow"));

            // Cancel and Send share the row evenly, Cancel first.
            Assert.Equal(cancel.ActualWidth, send.ActualWidth, 1);
            Assert.True(cancel.TranslatePoint(default, host).X < send.TranslatePoint(default, host).X);
            RenderFixture(host, "message-ready-to-send.png", 2);

            Click(send);
            Pump();

            // A yes takes the whole question away: what follows it in the answer says that it was sent, and shows the message.
            Assert.Equal(ToolConfirmationState.Approved, request.State);
            Assert.True(panel.IsGone);
            Assert.False(ButtonsVisible(host, "Send") || ButtonsVisible(host, "Cancel"));
            Assert.DoesNotContain("Ready to send it?", AllTextOf(host).Where(text => IsShown(host, text)));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            panel.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    // "Is Beeper your preferred way to message Sami?", as the send tool asks it once the user has answered which chat to use.
    private static ToolConfirmationRequested PreferenceRequest() =>
        new(
            ToolDefinition.Create("prefer_message_route", "Asks.", [], RiskLevel.SideEffect),
            new ToolCall("c1", "send_message", "{}"),
            Guid.NewGuid(),
            new ToolConfirmation(
                ConfirmationKind.Other, "Is Beeper your preferred way to message Sami?",
                [new ConfirmationDetail("To", "Sami"), new ConfirmationDetail("Through", "Beeper chat with Sami in Beeper")],
                "Yes", "Yes: messages to Sami go through it from now on.")
            {
                DeclineLabel = "No",
                ApprovedResult = "Kept. Their messages go through it from now on.",
                DeclinedResult = "Not kept. You are asked which one again next time.",
            });

    [Fact]
    public void TheQuestionAboutThePreferredChatIsAYesOrNo_AndSaysWhatTheAnswerDid() => RunSta(() =>
    {
        var yes = PreferenceRequest();
        using var asked = new ToolConfirmationContent(yes, TimeSpan.Zero);

        // A question like any other that is not the message itself: its own words on the buttons, and never "Always allow".
        Assert.True(asked.IsGeneral);
        Assert.False(asked.IsMessage);
        Assert.Equal(("Yes", "No"), (asked.ApproveLabel, asked.DeclineLabel));
        asked.ApproveCommand.Execute(null);
        Assert.Equal(ToolConfirmationState.Approved, yes.State);
        Assert.Equal("Kept. Their messages go through it from now on.", asked.ResultText);

        // Answered, it folds away behind the answer's three dots, as the other workings do.
        Assert.True(asked.IsTucked);

        var no = PreferenceRequest();
        using var declined = new ToolConfirmationContent(no, TimeSpan.Zero);
        declined.DeclineCommand.Execute(null);
        Assert.Equal("Not kept. You are asked which one again next time.", declined.ResultText);
    });

    [Fact]
    public void AMessageTheUserCancelsSaysItWasNotSent() => RunSta(() =>
    {
        var request = MessageRequest();
        using var panel = new ToolConfirmationContent(request, TimeSpan.Zero);

        panel.DeclineCommand.Execute(null);

        Assert.Equal(ToolConfirmationState.Declined, request.State);
        Assert.False(panel.IsGone);
        Assert.Equal("Not sent.", panel.ResultText);
    });

    private static bool IsShown(DependencyObject root, string text) =>
        Descendants<TextBlock>(root).Any(block => block.Text == text && block.IsVisible);

    [Fact]
    public void AMessageThatWasSentIsShownAsThePersonTheServiceAndTheBubble_AndTheAnswersCopyButtonCopiesIt() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var beeper = new SentMessageContent(new SentMessage("Sami Khan", "Beeper", "Beeper", "Hi", false, false));
        var imessage = new SentMessageContent(new SentMessage("Sami Khan", "iMessage", "Beeper", "On my way. See you at eight, and bring the charger if you can.", true, false));
        var hosts = new[] { beeper, imessage }.Select(card => new ContentControl { Content = card, Width = 386, Margin = new Thickness(0, 0, 0, 16) }).ToList();
        var stack = new StackPanel();
        hosts.ForEach(host => stack.Children.Add(host));
        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Padding = new Thickness(16), Child = stack },
            Width = 440, Height = 620, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            Pump();

            Assert.Equal(("Sami Khan", "SK", "Beeper", "Hi"), (beeper.To, beeper.Initials, beeper.Channel, beeper.Message));
            Assert.Equal(("iMessage", "Sending…"), (imessage.Channel, imessage.Note));
            var texts = AllTextOf(hosts[0]).ToList();
            Assert.Contains("Sami Khan", texts);
            Assert.Contains("Beeper", texts);
            Assert.Contains("Hi", texts);

            // The card has no button of its own: the copy button under the answer it is in copies what was sent, not the words about it.
            Assert.DoesNotContain(Descendants<Button>(hosts[0]), button => button.IsVisible);
            Assert.DoesNotContain(Descendants<Button>(hosts[1]), button => button.IsVisible);
            var answer = new MessageViewModel(MessageRole.Assistant, "It's sent.");
            answer.Content.Add(beeper);
            Assert.Equal("Hi", answer.CopyText);
            Assert.True(answer.CanCopy);
            Assert.Contains("Sending…", AllTextOf(hosts[1]));
            RenderFixture(stack, "message-sent.png", 2);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AMessageThatWasSentComesToTheTopOfTheView_WithWhatLedToItAboveAScrollAway() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        var messages = new System.Collections.ObjectModel.ObservableCollection<MessageViewModel> { new(MessageRole.User, "send a message to Sami saying hi") };
        var view = new ConversationView { Messages = messages, ContentPadding = new Thickness(30, 84.25, 30, 68), Width = 418, Height = 598 };
        var window = new Window { Content = view, Width = 460, Height = 660, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            // The answer as it streams: what was done, the words, and at its end the message as it went.
            var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
            answer.Content.Add(new TextContent("Looking for Sami's chat."));
            messages.Add(answer);
            Pump();
            answer.Content.Add(new TextContent("It's sent."));
            answer.Content.Add(new SentMessageContent(new SentMessage("Sami", "Beeper", "Beeper", "hi", false, false)));
            Pump();
            Pump();

            // "It's sent." stands where the first message rests, and the request and what led to the message are above it, out of view.
            var words = Descendants<ContentPresenter>(view).Last(presenter => presenter.Content is TextContent);
            Assert.Equal(84.25, words.TranslatePoint(default, view).Y, 0);
            Assert.True(view.Viewer.VerticalOffset > 0);
            Assert.True(view.TailRoom > 0);
            Assert.True(Descendants<SpeechBubble>(view).First().TranslatePoint(default, view).Y < 0);

            // A scroll up shows them again, and the next question gives the room back.
            view.Viewer.ScrollToVerticalOffset(0);
            Pump();
            Assert.True(Descendants<SpeechBubble>(view).First().TranslatePoint(default, view).Y > 0);
            messages.Add(new MessageViewModel(MessageRole.User, "thanks"));
            Pump();
            Assert.Equal(0, view.TailRoom);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void ADeviceInTheConversationShowsItsRow_AndItsStepsOnceItIsPressed() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var card = FanCard(new SwitchingHome(), new FixedRun("get_home_devices", "control_home_device"), "on");
        var host = new ContentControl { Content = card, Width = 386 };
        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Padding = new Thickness(16), Child = host },
            Width = 440, Height = 420, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            Pump();

            Assert.Contains("Window Fan", AllTextOf(host));
            Assert.DoesNotContain("Control a home device", AllTextOf(host).Where(text => IsShown(host, text)));

            card.OpenCommand.Execute(null);
            Pump();

            Assert.Contains("Control a home device", AllTextOf(host).Where(text => IsShown(host, text)));
            Assert.Contains("Look at your home devices", AllTextOf(host).Where(text => IsShown(host, text)));
            RenderFixture(host, "home-device-steps.png", 2);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            card.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    // ---- pictures from the clipboard ----

    [Fact]
    public void APictureFromTheClipboardIsHeldInMemoryAndGoesWithTheQuestion_WithNoFileRead() => RunSta(() =>
    {
        var pixels = new byte[64 * 48 * 4];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);

        var pasted = ClipboardPictures.FromBitmap(bitmap)!;

        Assert.True(pasted.IsPasted);
        Assert.False(pasted.IsCapture);
        Assert.Null(pasted.Path);
        Assert.Equal((64, 48, "Pasted image"), (pasted.PixelWidth, pasted.PixelHeight, pasted.Name));
        Assert.NotNull(pasted.Thumbnail);

        // What the model is given is a PNG of it.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], pasted.Data.Span[..4].ToArray());
        var chip = AttachmentChip.For(pasted);
        Assert.Contains("Pasted from the clipboard, 64 × 48 pixels", chip.Detail, StringComparison.Ordinal);

        // It is read with no permission to read files: nothing is read from disk for it.
        var read = new Assistant.UI.Search.AttachedImages(null).ReadAsync([pasted], CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(read.Problem);
        var item = Assert.Single(read.Items);
        Assert.Equal((ContextItemType.Image, pasted.Data.Length), (item.Type, item.ImageData.Length));
        Assert.Null(item.FilePath);
    });

    [Fact]
    public void PicturesPastedIntoTheBarStartAConversationThatHasThemAttached_WithWhatWasTypedInItsComposer() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant(new FakePlacement(), new StreamingAnswers { Hold = true }, animations: false);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            bar.Query = "what is this";
            var first = ClipboardPictures.FromBitmap(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr24, null, new byte[8 * 8 * 3], 24))!;
            var second = ClipboardPictures.FromBitmap(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr24, null, new byte[4 * 4 * 3], 12))!;

            // The bar's editor raises this when the user pastes with a picture on the clipboard.
            var raise = typeof(AssistantWindow).GetField("PicturesPasted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            ((EventHandler<IReadOnlyList<ImageItem>>)raise.GetValue(window)!).Invoke(window, [first, second]);

            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Equal(2, conversation.Chips.Count);
            Assert.Equal("what is this", conversation.Draft);
            Assert.Empty(conversation.Messages);
        }
        finally { window.Close(); }
    }));

    // ---- the wheel ----

    [Fact]
    public void TheWheelMovesTheConversationAsFarAsItWasTurned_AndASmallTurnMovesItALittle() => RunSta(() =>
    {
        var content = new Border { Height = 4000, Width = 300 };
        var viewer = new FadingScrollViewer { Content = content, Width = 300, Height = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var window = new Window { Content = viewer, Width = 340, Height = 460, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();
            Assert.True(viewer.ScrollableHeight > 0);

            void Turn(int delta)
            {
                content.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                Pump();
            }

            // It answers in the same frame, part of the way, and never goes farther than the wheel was turned.
            Turn(-120);
            Assert.InRange(viewer.VerticalOffset, 1, FadingScrollViewer.NotchDistance);
            var afterNotch = viewer.VerticalOffset;

            // A touchpad's small movement is a small movement: a plain scroll viewer would have moved a whole notch for it.
            Turn(-6);
            Assert.InRange(viewer.VerticalOffset - afterNotch, 0, FadingScrollViewer.NotchDistance);

            // Back up past the top stops at the top.
            for (var turns = 0; turns < 6; turns++)
            {
                Turn(240);
            }

            // The rest of the way is glided over the next frames.
            for (var until = Environment.TickCount64 + 3000; viewer.VerticalOffset > 1 && Environment.TickCount64 < until;)
            {
                Pump();
                Thread.Sleep(15);
            }

            Assert.InRange(viewer.VerticalOffset, 0, 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AScrollerInsideTheConversationTakesTheWheelOnlyWhileItHasSomewhereToGo() => RunSta(() =>
    {
        var inner = new ScrollViewer { Height = 100, Content = new Border { Height = 300 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var fixedInner = new ScrollViewer { Height = 100, Content = new Border { Height = 50 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var viewer = new FadingScrollViewer
        {
            Width = 300, Height = 300, Content = new StackPanel { Children = { inner, fixedInner, new Border { Height = 2000 } } },
        };
        var window = new Window { Content = viewer, Width = 340, Height = 360, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            void TurnDownOver(UIElement element)
            {
                element.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                Pump();
            }

            // Over a list that can scroll, the wheel is the list's.
            TurnDownOver((UIElement)inner.Content);
            Assert.Equal(0, viewer.VerticalOffset);

            // Over one that has nowhere to go, it scrolls the conversation: a card under the pointer never stops the scrolling.
            TurnDownOver((UIElement)fixedInner.Content);
            Assert.True(viewer.VerticalOffset > 0);
        }
        finally { window.Close(); }
    });
}
