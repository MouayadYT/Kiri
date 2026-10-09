using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Tools;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

// The words of a message the user may change before it is sent, and the row under every answer: the button that copies it, and the button with three
// dots that the answer's workings (its steps, the questions that were answered) fold into and out of.
public sealed partial class PromptInputControlTests
{
    private static ToolConfirmationRequested EditableMessageRequest(string text = "\"hello\"") =>
        new(
            ToolDefinition.Create("send_message", "Sends.", [], RiskLevel.SideEffect),
            new ToolCall("c1", "send_message", "{}"),
            Guid.NewGuid(),
            new ToolConfirmation(
                ConfirmationKind.SendMessage, "Send this message to Marcus?",
                [new ConfirmationDetail("To", "Marcus"), new ConfirmationDetail("Through", "Beeper chat with Marcus in Beeper"), new ConfirmationDetail("Message", text)],
                "Send", "A message cannot be taken back once it is sent.")
            {
                Edit = new ConfirmationEdit("Message", 4000),
                DeclineLabel = "Cancel",
            });

    [Fact]
    public void TheWordsOfTheMessageCanBeChangedInTheQuestion_AndWhatIsLeftThereIsWhatIsSent() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var request = EditableMessageRequest("hello");
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

            // The message is a field the user can click into, holding what was drafted.
            Assert.True(panel.CanEditMessage);
            var field = Assert.Single(Descendants<TextBox>(host), box => box.IsVisible);
            Assert.Equal("hello", field.Text);
            Assert.False(field.IsReadOnly);
            Assert.Equal(4000, field.MaxLength);
            Assert.True(field.AcceptsReturn);
            RenderFixture(host, "message-editable.png", 2);

            // An emptied field cannot be sent.
            field.Text = "   ";
            Pump();
            var send = Descendants<Button>(host).Single(button => button.IsVisible && button.Content as string == "Send");
            Assert.False(panel.CanApprove);
            Assert.False(send.IsEnabled);

            field.Text = "hello, on my way";
            Pump();
            Assert.Equal("hello, on my way", panel.MessageDraft);
            Assert.True(send.IsEnabled);
            Assert.Null(request.Confirmation.Edit!.Value);

            Click(send);
            Pump();

            // The yes carries the words that were left in the field; the field goes with the question.
            Assert.Equal(ToolConfirmationState.Approved, request.State);
            Assert.Equal("hello, on my way", request.Confirmation.Edit.Value);
            Assert.DoesNotContain(Descendants<TextBox>(host), box => box.IsVisible);
            Assert.True(errors.Messages.Count == 0, string.Concat(errors.Messages));
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            panel.Dispose();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AMessageThatWasNotChangedLeavesNothingInTheEdit_AndAQuestionWithoutOneShowsTheWordsOnly() => RunSta(() =>
    {
        var request = EditableMessageRequest("hello");
        using var panel = new ToolConfirmationContent(request, TimeSpan.Zero);

        panel.Approve();

        Assert.Equal(ToolConfirmationState.Approved, request.State);
        Assert.Null(request.Confirmation.Edit!.Value);

        // A question that came with no line to change (an older tool, a connected app's) is not one the user can write in, and is still sent.
        using var fixedWords = new ToolConfirmationContent(MessageRequest(), TimeSpan.Zero);
        Assert.False(fixedWords.CanEditMessage);
        Assert.True(fixedWords.CanApprove);
        using var note = new ToolConfirmationContent(NoteRequest(), TimeSpan.Zero);
        Assert.Equal(("Don't allow", "Cancel"), (note.DeclineLabel, panel.DeclineLabel));
    });

    // ---- the row under an answer ----

    private static (Window Window, ConversationView View, List<string> Copied) ShowConversation(params MessageViewModel[] messages)
    {
        var view = new ConversationView
        {
            Messages = new ObservableCollection<MessageViewModel>(messages), ContentPadding = new Thickness(30, 40, 30, 40), Width = 418, Height = 640,
        };
        var copied = new List<string>();
        view.CopyText = copied.Add;
        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Child = view },
            Width = 460, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        window.Show();
        Pump();
        return (window, view, copied);
    }

    private static bool Shown(DependencyObject root, string text) => Descendants<TextBlock>(root).Any(block => block.Text == text && block.IsVisible);

    [Fact]
    public void UnderAnAnswerIsAButtonThatCopiesIt_AndTheStepsAreBehindTheThreeDots_WhichOpenAndCloseThem() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        using var steps = new AgentTaskContent(new FixedRun("get_home_devices", "control_home_device"));
        answer.Content.Add(steps);
        var (window, view, copied) = ShowConversation(answer);
        try
        {
            // The steps are not among the answer's parts; while it is on its way, there is nothing to copy yet.
            Assert.False(Shown(view, "Control a home device"));
            Assert.False(answer.CanCopy);
            var more = Assert.Single(Descendants<ToggleButton>(view), button => button.IsVisible);
            Assert.Equal("Show what was done for this answer", System.Windows.Automation.AutomationProperties.GetName(more));

            answer.Content.Add(new TextContent("The fan is on."));
            answer.Status = MessageStatus.Complete;
            Pump();

            var copy = Assert.Single(Descendants<Button>(view), button => button.IsVisible && System.Windows.Automation.AutomationProperties.GetName(button) == "Copy this answer");
            Assert.True(copy.TranslatePoint(default, view).X < more.TranslatePoint(default, view).X);
            Assert.True(copy.TranslatePoint(default, view).Y > Descendants<TextBlock>(view).Single(block => block.Text == "The fan is on.").TranslatePoint(default, view).Y);
            RenderFixture(view, "answer-actions-closed.png", 2);

            Click(copy);
            Pump();
            Assert.Equal(["The fan is on."], copied);

            // The three dots open what was done, under the answer, and close it again.
            more.IsChecked = true;
            Pump();
            Assert.True(answer.IsDetailsOpen);
            Assert.True(Shown(view, "Control a home device"));
            Assert.True(Shown(view, "Look at your home devices"));
            RenderFixture(view, "answer-actions-open.png", 2);

            more.IsChecked = false;
            Pump();
            Assert.False(Shown(view, "Control a home device"));
            Assert.True(errors.Messages.Count == 0, string.Concat(errors.Messages));
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AQuestionThatWasAnsweredFoldsAwayBehindTheThreeDots_AndAMessageSentIsWhatTheCopyButtonCopies() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        using var question = new ToolConfirmationContent(NoteRequest(), TimeSpan.Zero);
        answer.Content.Add(question);
        var (window, view, copied) = ShowConversation(answer);
        try
        {
            // While it waits, the question stands in the answer, and the row has nothing in it yet.
            Assert.True(Shown(view, "Send this note to Omar?"));
            Assert.False(answer.HasActions);

            question.Approve();
            Pump();

            // Answered, it goes behind the three dots: the answer's parts no longer show it, and the dots do.
            Assert.True(question.IsTucked);
            Assert.Equal([question], answer.Details);
            Assert.False(Shown(view, "Send this note to Omar?"));
            answer.Content.Add(new TextContent("I shared it."));
            answer.Content.Add(new SentMessageContent(new SentMessage("Omar", "Beeper", "Beeper", "hello there", false, false)));
            answer.Status = MessageStatus.Complete;
            Pump();

            Click(Descendants<Button>(view).Single(button => button.IsVisible && System.Windows.Automation.AutomationProperties.GetName(button) == "Copy this answer"));
            Pump();
            Assert.Equal(["hello there"], copied);

            answer.IsDetailsOpen = true;
            Pump();
            Assert.True(Shown(view, "Send this note to Omar?"));
            Assert.True(Shown(view, "Allowed."));
            Assert.True(errors.Messages.Count == 0, string.Concat(errors.Messages));
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void AMessageAboutToBeSentIsNotPutAway_AndANoToItStaysWhereItIs() => RunSta(() =>
    {
        using var message = new ToolConfirmationContent(MessageRequest(), TimeSpan.Zero);
        var answer = new MessageViewModel(MessageRole.Assistant);
        answer.Content.Add(message);

        message.Decline();

        Assert.False(message.IsTucked);
        Assert.Empty(answer.Details);
        Assert.Equal("Not sent.", message.ResultText);
    });

    [Fact]
    public void APartOnScreenFoldsAwayAndBackOverAMoment_AndRestsAsItWas() => RunSta(() =>
    {
        var panel = new MessageBlockPanel { Spacing = 12 };
        var first = new Border { Height = 20, Background = Brushes.White };
        var part = new Border { Height = 60, Margin = new Thickness(-14, 0, -14, 0), Background = Brushes.Gray };
        var last = new Border { Height = 10, Background = Brushes.White };
        panel.Children.Add(first);
        panel.Children.Add(part);
        panel.Children.Add(last);
        var window = new Window { Content = panel, Width = 300, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();
            var lastTop = last.TranslatePoint(default, panel).Y;
            Assert.Equal(20 + 12 + 60 + 12, lastTop, 1);

            // Folded away, what was under it has come up into its place, gap and all; folded back, everything is where it was.
            Fold.SetIsOpen(part, false);
            Settle(TimeSpan.FromMilliseconds(500));
            Assert.Equal(Visibility.Collapsed, part.Visibility);
            Assert.Equal(20 + 12, last.TranslatePoint(default, panel).Y, 1);

            Fold.SetIsOpen(part, true);
            Settle(TimeSpan.FromMilliseconds(500));
            Assert.Equal(Visibility.Visible, part.Visibility);
            Assert.Equal(new Thickness(-14, 0, -14, 0), part.Margin);
            Assert.Null(part.Clip);
            Assert.Equal(1, part.Opacity);
            Assert.Equal(lastTop, last.TranslatePoint(default, panel).Y, 1);
        }
        finally
        {
            window.Close();
        }

        static void Settle(TimeSpan time)
        {
            var until = DateTime.UtcNow + time;
            while (DateTime.UtcNow < until)
            {
                Pump();
                Thread.Sleep(15);
            }

            Pump();
        }
    });

    [Fact]
    public void AFoldingPartTakesItsGapWithIt() => RunSta(() =>
    {
        var panel = new MessageBlockPanel { Spacing = 12, Width = 300 };
        var first = new Border { Height = 20 };
        var second = new Border { Height = 30 };
        var third = new Border { Height = 10 };
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(third);

        Measure(panel);
        Assert.Equal(20 + 12 + 30 + 12 + 10, panel.DesiredSize.Height);

        // Half folded: half its gap. Gone: none.
        Fold.SetAmount(second, 0.5);
        Measure(panel);
        Assert.Equal(20 + 6 + 30 + 12 + 10, panel.DesiredSize.Height);

        Fold.SetIsOpen(second, false);
        Measure(panel);
        Assert.Equal(Visibility.Collapsed, second.Visibility);
        Assert.Equal(20 + 12 + 10, panel.DesiredSize.Height);

        Fold.SetIsOpen(second, true);
        Measure(panel);
        Assert.Equal((Visibility.Visible, 1.0), (second.Visibility, Fold.GetAmount(second)));

        static void Measure(UIElement element)
        {
            element.InvalidateMeasure();
            element.Measure(new Size(300, double.PositiveInfinity));
        }
    });
}
