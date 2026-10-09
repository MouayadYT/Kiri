using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.Core.Domain;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Voice;
using Assistant.UI.Windowing;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void PanelLayoutMatchesTheReference() => RunSta(() => WithTheme(() =>
    {
        var placement = new FakePlacement();
        var (panel, model, _) = CreatePanel(placement);
        model.StartNew("I have a question");
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant, "What would you like to know?"));
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            Assert.Equal(new Size(418, 598), new Size(area.ActualWidth, area.ActualHeight));

            // The window is as wide as the pill and its gutter, so the panel sits centered in it, 96 from each side.
            Assert.Equal(new Size(610, 690), new Size(panel.ActualWidth, panel.ActualHeight));
            var (_, layout) = Assert.Single(placement.Calls);
            Assert.Equal(new OverlayLayout(610, 690, 96, 28, 418, 598, 0.22), layout);
            Assert.Equal(AssistantWindowState.FloatingConversation, panel.State);

            // The glass is the panel, right under the conversation's layer.
            var host = Named<Grid>(panel, "SurfaceHost");
            var glass = Named<Grid>(panel, "SurfaceArea");
            Assert.Equal(new Rect(96, 28, 418, 598), BoundsIn(host, glass));
            Assert.Equal(BoundsIn(host, glass), BoundsIn(host, area));

            // Glass buttons in the four corners, 16 DIPs in.
            Assert.Equal(new Rect(16, 16, 36, 36), BoundsIn(area, Named<Button>(panel, "CloseButton")));
            Assert.Equal(new Rect(366, 16, 36, 36), BoundsIn(area, Named<Button>(panel, "ExpandButton")));
            // The composer shows under an answer, so the plus stands at its left, where the speaker does while an answer comes.
            Assert.Equal(new Rect(16, 546, 36, 36), BoundsIn(area, Named<Button>(panel, "AddButton")));
            Assert.Equal(Visibility.Collapsed, Named<Button>(panel, "SpeakerButton").Visibility);
            Assert.True(Named<Button>(panel, "AddButton").IsEnabled);
            Assert.Equal(1, Named<Button>(panel, "AddButton").Opacity);
            Assert.Equal(new Rect(366, 546, 36, 36), BoundsIn(area, Named<Button>(panel, "VoiceButton")));

            // The conversation on the glass, resting between them: the user's bubble on the right, the answer as text
            // on the left.
            Assert.Equal(new Rect(0, 0, 418, 598), BoundsIn(area, Named<FadingScrollViewer>(panel, "Transcript")));
            var bubble = Assert.Single(Descendants<SpeechBubble>(area));
            var bubbleBounds = BoundsIn(area, bubble);
            Assert.Equal(84.25, bubbleBounds.Top, 2);
            Assert.Equal(388, bubbleBounds.Right, 1);
            Assert.InRange(bubbleBounds.Height, 31, 32);
            var answer = Descendants<TextBlock>(area).Single(text => text.Text == "What would you like to know?");
            Assert.Equal(30, BoundsIn(area, answer).Left, 1);
            Assert.Equal(bubbleBounds.Bottom + 40, BoundsIn(area, answer).Top, 1);
            Assert.Equal(15, answer.FontSize);
            Assert.Equal(13.5, Descendants<TextBlock>(bubble).Single().FontSize);

            // The same glass as the bar, and no glow while the microphone is off.
            var surface = Named<SurfaceShape>(panel, "Surface");
            Assert.Equal(Stops("Brush.Surface.GlassOpaque"), Stops(surface.Fill));
            Backdrop.SetIsBlurred(panel, true);
            Assert.Equal(Stops("Brush.Surface.Glass"), Stops(surface.Fill));
            Assert.Equal(SurfaceForm.Panel(418, 598, 42.75), surface.Form);
            foreach (var name in new[] { "BarGlow", "PanelGlow" })
            {
                var glow = Named<VoiceGlow>(panel, name);
                Assert.False(glow.IsActive);
                Assert.Equal(0, glow.Presence);
            }

            Assert.Equal(new Rect(0, 0, 418, 598), Named<Grid>(panel, "GlowLayer").Clip.Bounds);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ThePlusStandsAtTheLeftWhileTheComposerShows_AndTheSpeakerWhileItDoesNot() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        try
        {
            // Nothing asked yet and nothing attached, as when an answer is on its way: no composer, so the speaker.
            panel.ShowConversation();
            Pump();
            var speaker = Named<Button>(panel, "SpeakerButton");
            var plus = Named<Button>(panel, "AddButton");
            Assert.False(model.CanCompose);
            Assert.True(speaker.IsVisible);
            Assert.False(plus.IsVisible);

            // A message in the conversation brings the composer, and the plus takes the speaker's place in the same row.
            model.Messages.Add(new MessageViewModel(MessageRole.User, "A question"));
            Pump();
            Assert.True(model.CanCompose);
            Assert.False(speaker.IsVisible);
            Assert.True(plus.IsVisible);
            Assert.Equal("Add to the conversation", AutomationName(plus));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void PanelButtonsWorkOrWaitForTheirFeature() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, microphone) = CreatePanel();
        model.StartNew("question");
        try
        {
            panel.ShowConversation();
            Pump();
            // The speaker button belongs to the Assistant's voice (step 125): with no voice wired in, nothing can be said and it waits, dimmed.
            var speaker = Named<Button>(panel, "SpeakerButton");
            Assert.NotNull(speaker.Command);
            Assert.False(speaker.IsEnabled);

            // The expand button asks for the conversation to go on in the History window.
            var expand = Named<Button>(panel, "ExpandButton");
            Assert.True(expand.IsEnabled);
            Assert.Equal("Open in History window", AutomationName(expand));
            var requests = 0;
            model.OpenInHistoryRequested += (_, _) => requests++;
            expand.Command.Execute(null);
            Assert.Equal(1, requests);

            // The voice button offers the microphone, then the keyboard while listening.
            var voice = Named<Button>(panel, "VoiceButton");
            Assert.True(voice.IsEnabled);
            Assert.Equal("Use microphone", AutomationName(voice));
            voice.Command.Execute(null);
            Pump();
            Assert.True(model.Voice.IsListening);
            Assert.Single(microphone.Sessions);
            Assert.Equal("Use keyboard", AutomationName(voice));
            Assert.True(Named<VoiceGlow>(panel, "PanelGlow").IsActive);
            voice.Command.Execute(null);
            Pump();
            Assert.False(model.Voice.IsListening);
            Assert.Equal("Use microphone", AutomationName(voice));

            // Close dismisses; the window and its conversation remain, and the next time it opens as the bar.
            var handle = new WindowInteropHelper(panel).Handle;
            model.Voice.Start();
            var close = Named<Button>(panel, "CloseButton");
            Assert.True(((RoutedCommand)close.Command).CanExecute(null, close));
            ((RoutedCommand)close.Command).Execute(null, close);
            Assert.False(model.Voice.IsListening);
            WaitUntil(() => !panel.IsVisible, "The close button did not dismiss the panel.");
            Assert.Single(model.Messages);
            Assert.Equal(AssistantWindowState.Compact, panel.State);
            panel.ShowAndFocus();
            Assert.Equal(handle, new WindowInteropHelper(panel).Handle);
            Assert.True(panel.IsVisible);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void PanelEscapeStopsVoiceThenDismissesAndAltF4OnlyHides() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("question");
        try
        {
            panel.ShowConversation();
            Pump();
            model.Voice.Start();
            PressEscape(panel);
            Assert.False(model.Voice.IsListening);
            Assert.True(panel.IsVisible);
            PressEscape(panel);
            WaitUntil(() => !panel.IsVisible, "Escape did not dismiss the panel.");

            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not come back.");
            SendMessage(new WindowInteropHelper(panel).Handle, 0x0112, 0xF060, 0); // WM_SYSCOMMAND, SC_CLOSE.
            WaitUntil(() => !panel.IsVisible, "Alt+F4 did not dismiss the panel.");
            panel.ShowAndFocus();
            Assert.True(panel.IsVisible);
            Assert.Single(model.Messages);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void PanelStaysOpenWhenFocusMovesAwayButStopsListening() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("question");
        var other = new Window { Left = -10000, Top = -10000, Width = 50, Height = 50, Opacity = 0, ShowInTaskbar = false };
        try
        {
            panel.ShowConversation();
            Assert.True(panel.IsActive, "The panel must be active for this test.");
            model.Voice.Start();
            other.Show();
            other.Activate();
            WaitUntil(() => !model.Voice.IsListening, "Losing focus did not stop the microphone.");
            Pump();
            Assert.True(panel.IsVisible);
            Assert.Single(model.Messages);
        }
        finally
        {
            other.Close();
            panel.Close();
        }
    }));

    [Fact]
    public void NewMessagesScrollIntoView() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("question");
        try
        {
            panel.ShowConversation();
            for (var i = 0; i < 12; i++)
            {
                model.Messages.Add(new MessageViewModel(i % 2 == 0 ? MessageRole.Assistant : MessageRole.User, $"Synthetic message {i}"));
            }

            var transcript = Named<FadingScrollViewer>(panel, "Transcript");
            WaitUntil(() => transcript.ScrollableHeight > 0 && transcript.VerticalOffset == transcript.ScrollableHeight,
                "The newest message was not scrolled into view.");
            Assert.Equal(13, Descendants<TextBlock>(transcript).Count(text => text.Text.Length > 0));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void MessagesScrollUnderTheButtonsAndFadeIntoTheGlass() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew(string.Join(" ", Enumerable.Repeat("A synthetic question, long enough to wrap.", 8)));
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant, string.Join("\n\n", Enumerable.Range(1, 40)
            .Select(i => $"Synthetic paragraph {i}, long enough to wrap across more than one line of the conversation."))));
        try
        {
            panel.ShowConversation();
            var area = Named<Grid>(panel, "ConversationLayer");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            var transcript = Named<FadingScrollViewer>(panel, "Transcript");

            // No surface of its own and no edge but the panel's: transparent, and clipped to the panel's outline.
            Assert.Equal(new Rect(0, 0, 418, 598), BoundsIn(area, transcript));
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(transcript.Background).Color.A);
            Assert.Null(Named<ItemsControl>(panel, "MessageList").Background);
            var clip = Named<ConversationView>(panel, "Conversation").Clip;
            Assert.Equal(new Rect(0, 0, 418, 598), clip.Bounds);
            Assert.False(clip.FillContains(new Point(3, 3)));
            Assert.True(clip.FillContains(new Point(209, 1)));

            // Gone at the panel's edges and clear between the buttons, where the first message rests almost clear.
            double Opacity(double y) => FadingScrollViewer.GetOpacity(y, 598, transcript.TopFade, transcript.BottomFade);
            Assert.All(new[] { 0.0, 16, 598 - 14, 598 }, y => Assert.Equal(0, Opacity(y)));
            Assert.All(new[] { 88.0, 299, 598 - 88 }, y => Assert.Equal(1, Opacity(y)));
            Assert.InRange(Opacity(84.25), 0.9, 1);

            // Scrolled, the messages are drawn under both rows of buttons, fading as the fades allow, and nothing is
            // drawn outside their column, such as a surface behind them.
            Assert.True(transcript.ScrollableHeight > 2 * 422, "The conversation is too short for this test.");
            Backdrop.SetIsBlurred(panel, true);
            transcript.ScrollToVerticalOffset(90);
            Pump();
            RenderGlass(panel, "conversation-scrolled-top.png", 2);
            transcript.ScrollToVerticalOffset(transcript.ScrollableHeight / 2);
            Pump();
            RenderGlass(panel, "conversation-scrolled.png", 2);
            foreach (UIElement layer in area.Children)
            {
                if (layer != Named<ConversationView>(panel, "Conversation")) layer.Visibility = Visibility.Hidden;
            }

            Pump();
            var alpha = RenderAlpha(area);
            int RowMax(int y) => Enumerable.Range(0, alpha.GetLength(1)).Max(x => alpha[y, x]);
            for (var y = 0; y < alpha.GetLength(0); y++)
            {
                Assert.True(RowMax(y) <= Math.Max(Opacity(y), Opacity(y + 1)) * 255 + 3, $"Row {y} is not faded enough.");
                Assert.True(Enumerable.Range(0, 16).Concat(Enumerable.Range(403, 15)).All(x => alpha[y, x] == 0),
                    $"Something is drawn beside the messages in row {y}.");
            }

            Assert.Contains(Enumerable.Range(88, 422), y => RowMax(y) >= 250);
            Assert.Contains(Enumerable.Range(20, 60), y => RowMax(y) is > 20 and < 200);
            Assert.Contains(Enumerable.Range(598 - 80, 60), y => RowMax(y) is > 20 and < 200);

            // Paging moves by the clear height between the fades, so no line is passed over while it is faded.
            transcript.ScrollToVerticalOffset(0);
            Pump();
            Assert.Equal((0, 598), (transcript.VerticalOffset, transcript.ViewportHeight));
            PressKey(transcript, Key.PageDown);
            Pump();
            Assert.Equal(598 - 88 - 88, transcript.VerticalOffset, 3);
            PressKey(transcript, Key.PageDown);
            Pump();
            Assert.Equal(2 * (598 - 88 - 88), transcript.VerticalOffset, 3);
            PressKey(transcript, Key.PageUp);
            Pump();
            Assert.Equal(598 - 88 - 88, transcript.VerticalOffset, 3);
            System.Windows.Controls.Primitives.ScrollBar.PageUpCommand.Execute(null, transcript);
            Pump();
            Assert.Equal(0, transcript.VerticalOffset, 3);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void AskingGrowsTheBarIntoTheConversationAndTheHotkeyReturnsToIt() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant(animations: false);
        var (window, barModel, conversation) = (assistant.Window, assistant.Bar, assistant.Conversation);
        try
        {
            assistant.Controller.Invoke();
            Assert.True(window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);

            // Enter in the bar asks, with voice input on: the same window takes the question and keeps listening.
            barModel.Voice.Start();
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "Synthetic question";
            Assert.True(input.TrySubmit());
            Assert.True(window.IsVisible);
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Equal(AssistantWindowState.FloatingConversation, assistant.Controller.State);
            var message = Assert.Single(conversation.Messages);
            Assert.Equal((MessageRole.User, "Synthetic question"), (message.Role, message.Text));
            Assert.Equal("", barModel.Query);
            Assert.False(barModel.Voice.IsListening);
            Assert.True(conversation.Voice.IsListening);

            // While the conversation is open, the hotkey brings it back rather than opening the bar.
            conversation.Voice.Stop();
            assistant.Controller.Invoke();
            Assert.True(window.IsVisible);
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);

            // Once it is dismissed, the hotkey opens the bar again, in the very same window.
            var handle = new WindowInteropHelper(window).Handle;
            window.Dismiss();
            WaitUntil(() => !window.IsVisible, "The conversation was not dismissed.");
            Assert.Equal(AssistantWindowState.Compact, window.State);
            assistant.Controller.Invoke();
            Assert.True(window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            Assert.Single(Application.Current.Windows.OfType<AssistantWindow>());
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void DraggedPositionCarriesFromBarToPanelUntilTheAssistantIsDismissed() => RunSta(() => WithTheme(() =>
    {
        var placement = new FakePlacement();
        var assistant = CreateAssistant(placement, animations: false);
        var (window, controller) = (assistant.Window, assistant.Controller);
        try
        {
            // Alt+A opens the bar at its default place.
            controller.Invoke();
            Assert.Single(placement.Calls);
            Assert.Empty(placement.PointCalls);

            // A dragged bar is kept on screen where it was dropped, and asking grows it into the panel right there.
            var dropped = new ScreenPoint(-700, 300);
            placement.AnchorTop = dropped;
            window.OnDragged();
            Assert.Equal((91.0, dropped), (placement.PointCalls[^1].Layout.AnchorHeight, placement.PointCalls[^1].Point));
            Assert.Equal(dropped, controller.DraggedTo);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "Synthetic question";
            Assert.True(input.TrySubmit());
            Assert.True(window.IsVisible);
            Assert.Single(placement.PointCalls);
            Assert.Single(placement.Calls);
            Assert.Equal(dropped, controller.DraggedTo);

            // The hotkey moves an open panel back onto the pointer's monitor.
            var moved = new ScreenPoint(1200, 150);
            placement.AnchorTop = moved;
            window.OnDragged();
            Assert.Equal((598.0, moved), (placement.PointCalls[^1].Layout.AnchorHeight, placement.PointCalls[^1].Point));
            Assert.Equal(moved, controller.DraggedTo);
            var placed = placement.PointCalls.Count;
            controller.Invoke();
            Assert.Equal(placed, placement.PointCalls.Count);
            Assert.Equal(2, placement.Calls.Count);
            Assert.Null(controller.DraggedTo);

            // Once nothing is showing, the position is forgotten and Alt+A opens the bar at its default place again.
            window.Dismiss();
            WaitUntil(() => !window.IsVisible, "The panel was not dismissed.");
            Assert.Null(controller.DraggedTo);
            controller.Invoke();
            Assert.Equal(3, placement.Calls.Count);
            Assert.Equal(placed, placement.PointCalls.Count);

            // Likewise when the bar alone was dragged and then dismissed.
            window.OnDragged();
            Assert.Equal(moved, controller.DraggedTo);
            window.Dismiss();
            WaitUntil(() => !window.IsVisible, "The bar was not dismissed.");
            Assert.Null(controller.DraggedTo);
            controller.Invoke();
            Assert.Equal(4, placement.Calls.Count);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void OnlyTheGlassStartsADrag() => RunSta(() => WithTheme(() =>
    {
        var (window, _, model) = CreateAssistant();
        model.StartNew("Synthetic question");
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant, "Synthetic answer."));
        try
        {
            window.ShowAndFocus();
            Pump();
            static bool Bar(DependencyObject element) => SurfaceDrag.CanDragFrom(element, _ => false);

            // The glass and the space around the pill's editor move it; typing, the caret and the microphone do not.
            var input = Named<PromptInputControl>(window, "PromptInput");
            Assert.True(Bar(Named<SurfaceShape>(window, "Surface")));
            Assert.True(Bar(Descendants<Grid>(input).First()));
            Assert.False(Bar(Part<TextBox>(input, "PART_Editor")));
            Assert.False(Bar(Descendants<ContentPresenter>(Part<Button>(input, "Microphone")).First()));
            Assert.False(Bar(new Hyperlink()));

            // The panel's glass and the empty space in its conversation move it; messages and buttons do not.
            window.ShowConversation();
            Pump();
            var area = Named<Grid>(window, "ConversationLayer");
            bool Panel(DependencyObject element) => SurfaceDrag.CanDragFrom(element, window.IsMessage);
            Assert.True(Panel(Named<SurfaceShape>(window, "Surface")));
            Assert.True(Panel(Named<FadingScrollViewer>(window, "Transcript")));
            Assert.True(Panel(Descendants<ScrollContentPresenter>(Named<FadingScrollViewer>(window, "Transcript")).Single()));
            Assert.False(Panel(Part<TextBox>(Named<PromptInputControl>(window, "ComposerInput"), "PART_Editor")));
            Assert.False(Panel(Descendants<TextBlock>(area).Single(text => text.Text == "Synthetic answer.")));
            Assert.False(Panel(Descendants<TextBlock>(Descendants<SpeechBubble>(area).Single()).Single()));
            Assert.False(Panel(Named<Button>(window, "CloseButton")));
            Assert.False(Panel(Descendants<ContentPresenter>(Named<Button>(window, "VoiceButton")).First()));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void BarMicrophoneTogglesItsGlowAndDismissingStopsListening() => RunSta(() => WithTheme(() =>
    {
        var microphone = new FakeMicrophone();
        var (window, model, _) = CreateAssistant(barMicrophone: microphone);
        try
        {
            window.ShowAndFocus();
            Pump();
            var input = Named<PromptInputControl>(window, "PromptInput");
            var button = Part<Button>(input, "Microphone");
            Assert.True(button.IsEnabled);
            button.Command.Execute(null);
            Pump();
            Assert.True(model.Voice.IsListening);
            Assert.True(input.IsMicrophoneActive);
            Assert.True(Named<VoiceGlow>(window, "BarGlow").IsActive);
            Assert.False(Named<VoiceGlow>(window, "PanelGlow").IsActive);
            Assert.Equal(Color.FromRgb(0xF5, 0xF5, 0xF5), Assert.IsType<SolidColorBrush>(button.Foreground).Color);
            Assert.Equal(new Rect(0, 0, 520, 91), Named<Grid>(window, "GlowLayer").Clip.Bounds);

            window.Dismiss();
            Assert.False(model.Voice.IsListening);
            Assert.True(Assert.Single(microphone.Sessions).IsDisposed);
            WaitUntil(() => !window.IsVisible, "The bar was not dismissed.");

            // Hiding it any other way stops listening too.
            window.ShowAndFocus();
            model.Voice.Start();
            window.Hide();
            Assert.False(model.Voice.IsListening);
        }
        finally { window.Close(); }
    }));

    // The Assistant's one window with its two view models and the controller that moves it between its states. Frames
    // are the frame sources it was given, the show and hide transition's first and the growth's second, for a test to
    // drive by hand; without them the window runs on WPF's own.
    private sealed record AssistantSetup(
        AssistantWindow Window, SearchOrAskViewModel Bar, ConversationViewModel Conversation,
        FakeMicrophone BarMicrophone, FakeMicrophone ConversationMicrophone, AssistantWindowStateController Controller,
        FakeBackdropFactory Backdrops)
    {
        public void Deconstruct(out AssistantWindow window, out SearchOrAskViewModel bar, out ConversationViewModel conversation)
        {
            window = Window;
            bar = Bar;
            conversation = Conversation;
        }
    }

    private static AssistantSetup CreateAssistant(
        IWindowPlacementService? placement = null, IAnswerProvider? answers = null, List<FakeFrames>? frames = null,
        bool animations = true, FakeMicrophone? barMicrophone = null, LauncherViewModel? launcher = null,
        SearchResultsViewModel? results = null, ActivityViewModel? activity = null,
        Assistant.Core.Contracts.IFileLauncher? fileLauncher = null, Assistant.UI.Search.AttachRequests? attachRequests = null,
        Assistant.Core.QuickSearch.Routing.IQueryRouter? router = null, Assistant.UI.Search.QuickSearchActionRunner? runner = null,
        IVoiceInput? voiceInput = null, ISpokenAnswers? speech = null)
    {
        var barMic = barMicrophone ?? new FakeMicrophone();
        var conversationMic = new FakeMicrophone();
        var bar = CreateBarModel(barMic, launcher, results, activity, router, voiceInput, speech);
        var conversation = CreateConversationModel(conversationMic, answers, activity, voiceInput, speech);
        Func<IFrameSource> source = frames is null
            ? () => new RenderingFrameSource()
            : () =>
            {
                var fake = new FakeFrames();
                frames.Add(fake);
                return fake;
            };
        var backdrops = new FakeBackdropFactory();
        var window = new AssistantWindow(
            bar, conversation, backdrops, placement ?? new FakePlacement(), source,
            animations ? () => SystemParameters.ClientAreaAnimation || frames is not null : () => false, fileLauncher, speech)
        {
            Left = -10000, Top = -10000, Opacity = 0,
        };
        return new AssistantSetup(window, bar, conversation, barMic, conversationMic,
            new AssistantWindowStateController(window, bar, conversation, attachRequests, runner, speech), backdrops);
    }

    // The window for a test of the conversation alone: a test shows it with ShowConversation.
    private static (AssistantWindow Panel, ConversationViewModel Model, FakeMicrophone Microphone) CreatePanel(
        FakePlacement? placement = null, IAnswerProvider? answers = null)
    {
        var assistant = CreateAssistant(placement, answers);
        return (assistant.Window, assistant.Conversation, assistant.ConversationMicrophone);
    }

    private static void WithTheme(Action action)
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        try { action(); }
        finally { app.Resources.MergedDictionaries.Clear(); }
    }

    // An element by name, in the scope's own names or, for one inside a view of its own such as the conversation view,
    // anywhere below it.
    private static T Named<T>(FrameworkElement scope, string name) where T : class =>
        Assert.IsType<T>(scope.FindName(name) ?? NamedBelow(scope, name));

    private static object? NamedBelow(DependencyObject scope, string name)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(scope).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Name: var childName } element && childName == name)
            {
                return element;
            }

            if (NamedBelow(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    // A gradient's stops, from a theme key or a brush, so brushes from separately loaded dictionaries compare equal.
    private static (double, Color)[] Stops(object? keyOrBrush)
    {
        var brush = keyOrBrush is string key ? Application.Current.FindResource(key) : keyOrBrush;
        return Assert.IsAssignableFrom<GradientBrush>(brush).GradientStops.Select(stop => (stop.Offset, stop.Color)).ToArray();
    }

    private static string AutomationName(DependencyObject element) =>
        System.Windows.Automation.AutomationProperties.GetName(element);

    private static void PressKey(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });

    private static void PressEscape(Window window)
    {
        var target = Keyboard.FocusedElement as UIElement ?? window;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
