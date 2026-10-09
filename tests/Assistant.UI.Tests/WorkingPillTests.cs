using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Activity;
using Assistant.Core.Domain;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The Working pill (PROJECT_SPEC §4.1, §4.2), as the answer reference shows it: the bar folds into a pill that says what the Assistant is
    // ---- doing while the answer is on its way, and the pill springs open into the conversation when it comes. Timings are the recording's.

    [Fact]
    public void TheSpringsGoAsFarPastTheEndAndSettleAsSoonAsTheReferenceRecordingShows()
    {
        // Opening: the panel was farthest past its size, about 3.5 % of the way, 0.37 s after it started, and at rest by about 0.75 s.
        var open = new SpringMotion { DampingRatio = 0.73, PeakTime = TimeSpan.FromMilliseconds(370) };
        Assert.InRange(open.Overshoot, 0.030, 0.040);
        Assert.Equal(1 + open.Overshoot, open.ValueAt(open.PeakTime), 3);
        Assert.InRange(open.SettleTime.TotalMilliseconds, 650, 800);

        // As measured frame by frame: 46 % of the way after 0.1 s, 87 % after 0.2 s.
        Assert.InRange(open.ValueAt(TimeSpan.FromMilliseconds(100)), 0.38, 0.50);
        Assert.InRange(open.ValueAt(TimeSpan.FromMilliseconds(200)), 0.82, 0.90);

        // Folding: the pill was about 1 % of the way narrower than it ends up, 0.15 s in, and at rest by 0.23 s.
        var fold = new SpringMotion { DampingRatio = 0.82, PeakTime = TimeSpan.FromMilliseconds(150) };
        Assert.InRange(fold.Overshoot, 0.008, 0.016);
        Assert.InRange(fold.SettleTime.TotalMilliseconds, 200, 260);

        // A spring starts where it is, never goes backwards before its peak, and rests exactly at the end.
        Assert.Equal(0, open.ValueAt(TimeSpan.Zero));
        Assert.Equal(0, open.ValueAt(TimeSpan.FromMilliseconds(-5)));
        var previous = 0.0;
        for (var ms = 5; ms <= 370; ms += 5)
        {
            var value = open.ValueAt(TimeSpan.FromMilliseconds(ms));
            Assert.True(value > previous, $"The spring went back at {ms} ms.");
            previous = value;
        }

        Assert.Equal(1, open.ValueAt(open.SettleTime));
        Assert.Equal(1, open.ValueAt(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AFormBetweenTwoMayGoALittlePastEitherAsASpringDoes()
    {
        var pill = SurfaceForm.Pill(148, 53);
        Assert.Equal(pill, SurfaceForm.Interpolate(pill, ConversationForm, 0));
        Assert.Equal(ConversationForm, SurfaceForm.Interpolate(pill, ConversationForm, 1));

        var past = SurfaceForm.Interpolate(pill, ConversationForm, 1.035);
        Assert.True(past.Width > ConversationForm.Width && past.Height > ConversationForm.Height);
        Assert.Equal(598 + (0.035 * (598 - 53)), past.Height, 6);

        // Never less than nothing, however far a spring throws it.
        var before = SurfaceForm.Interpolate(pill, ConversationForm, -5);
        Assert.True(before.Width >= 1 && before.Height >= 1 && before.CornerWidth >= 0 && before.CornerHeight >= 0);
    }

    [Fact]
    public void AskingFoldsTheBarIntoTheWorkingPillAndTheAnswerSpringsItOpenIntoThePanel() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var placement = new FakePlacement();
        var assistant = CreateAssistant(placement, answers, frames);
        var (window, bar, conversation) = assistant;
        var expanded = 0;
        window.Expanded += (_, _) => expanded++;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Grid Layer(string name) => GridNamed(window, name);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "hello there";
            Assert.True(input.TrySubmit());

            // At once: the question is asked and its answer is waited for; the bar is pressed narrower, where it hangs, and its words dim.
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.True(window.IsWaitingForAnswer);
            Assert.False(window.IsExpanding);
            Assert.Equal("Working", window.WorkingText);
            Assert.Equal("hello there", Assert.Single(conversation.Messages).Text);
            var pressed = GlassRegion(window);
            Assert.Equal(520 * 0.878, pressed.Width, 3);
            Assert.Equal(88, pressed.Height, 3);
            Assert.Equal(305, pressed.Left + (pressed.Width / 2), 3);
            Assert.Equal(28, pressed.Top, 3);
            Assert.Equal(0.5, Layer("CompactLayer").Opacity, 3);
            Assert.Equal(0, expanded);
            RenderGlass(window, "pill-00-pressed.png", 2);

            // It is held pressed for 65 ms, and then springs into the pill.
            var morph = frames[1];
            morph.Tick(T0);
            morph.RunUntil(T0 + TimeSpan.FromMilliseconds(60));
            Assert.Equal(pressed.Width, GlassRegion(window).Width, 3);

            var widths = new List<double>();
            while (morph.Running)
            {
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(5));
                var glass = GlassRegion(window);
                widths.Add(glass.Width);
                Assert.Equal(305, glass.Left + (glass.Width / 2), 3);
                Assert.Equal(28, glass.Top, 3);
                Assert.Equal(window.Form.Width, glass.Width, 3);
                Assert.InRange(widths.Count, 1, 200);
                if (widths.Count % 6 == 0)
                {
                    RenderGlass(window, $"pill-{widths.Count:D2}-fold.png", 2);
                }
            }

            // The pill: 53 tall, as wide as its words need, hanging from the bar's line. It went about 1 % of the way past and came back, and the
            // whole fold took about a quarter of a second after the press.
            var pill = GlassRegion(window);
            Assert.Equal(53, pill.Height, 3);
            Assert.InRange(pill.Width, 125, 175);
            Assert.Equal(SurfaceForm.Pill(pill.Width, 53), window.Form);
            var travel = pressed.Width - pill.Width;
            Assert.InRange(pill.Width - widths.Min(), travel * 0.004, travel * 0.03);
            Assert.InRange((widths.Count * 5) + 60, 250, 340);
            Assert.True(window.IsWaitingForAnswer);
            Assert.False(window.IsExpanding);

            // Only the pill's contents show: the spinner and the words. The bar's are gone, and the conversation's are still clear.
            Assert.Equal(Visibility.Collapsed, Layer("CompactLayer").Visibility);
            Assert.Equal((Visibility.Visible, 1.0), (Layer("WorkingLayer").Visibility, Layer("WorkingLayer").Opacity));
            Assert.Equal(pill.Width, Layer("WorkingLayer").ActualWidth, 3);
            Assert.True(Named<SearchingIndicator>(window, "WorkingIndicator").IsActive);
            Assert.Equal(0, Layer("ConversationLayer").Opacity);
            Assert.False(Layer("ConversationLayer").IsHitTestVisible);
            Assert.False(Named<ConversationView>(window, "Conversation").IsWorking);
            Assert.Equal("hello there", bar.Query);
            Assert.Equal(0, expanded);
            // The words come in out of a blur on WPF's own clock: for the picture, they are drawn as they rest.
            var words = Named<Border>(window, "WorkingLabelHost");
            words.BeginAnimation(UIElement.OpacityProperty, null);
            words.Opacity = 1;
            words.Effect = null;
            RenderGlass(window, "pill-working.png", 2);

            // The answer's first words open the pill into the panel: a spring that goes a little past the panel and settles.
            answers.Show();
            Assert.False(window.IsWaitingForAnswer);
            Assert.True(window.IsExpanding);
            Assert.Equal(2, conversation.Messages.Count);
            morph.Tick(morph.Last + TimeSpan.FromSeconds(1));
            var heights = new List<double>();
            var host = Layer("SurfaceHost");
            while (window.IsExpanding)
            {
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(5));
                var glass = GlassRegion(window);
                heights.Add(glass.Height);
                Assert.Equal(305, glass.Left + (glass.Width / 2), 3);
                Assert.Equal(28, glass.Top, 3);
                Assert.InRange(heights.Count, 1, 400);

                // Past the panel's size the conversation's contents stretch with the glass, so they bounce with it; before, they keep their size.
                var contents = BoundsIn(host, Layer("ConversationLayer"));
                if (glass.Height > 598 && glass.Width > 418)
                {
                    Assert.Equal(glass.Height, contents.Height, 3);
                    Assert.Equal(glass.Width, contents.Width, 3);
                    Assert.Equal(28, contents.Top, 3);
                }
                else if (glass.Height < 598 && glass.Width < 418)
                {
                    Assert.Equal(new Size(418, 598), contents.Size);
                }
                if (heights.Count % 12 == 0)
                {
                    RenderGlass(window, $"pill-open-{heights.Count:D3}.png", 2);
                }
            }

            // Farthest about 3.5 % of the way past the panel, about 0.37 s in, and at rest by about 0.75 s, as measured from the reference.
            var grown = 598.0 - 53;
            var peak = heights.Max();
            Assert.InRange(peak - 598, grown * 0.025, grown * 0.045);
            Assert.InRange((heights.IndexOf(peak) + 1) * 5, 340, 400);
            Assert.InRange(heights.Count * 5, 650, 820);

            // It never shrinks on the way up, and on the way back it settles with no more than a pixel's dip below the panel, as the
            // reference's does (a spring's second swing, a thirtieth of its first).
            var rising = heights.Take(heights.IndexOf(peak) + 1).ToList();
            Assert.Equal(rising.Order(), rising);
            Assert.All(heights.Skip(heights.IndexOf(peak)), height => Assert.True(height >= 598 - 1));

            // At rest it is the conversation, as when the bar grows into it: the panel, its contents alone, the bar emptied for next time.
            Assert.Equal(ConversationForm, window.Form);
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));
            Assert.Equal((Visibility.Collapsed, Visibility.Collapsed, Visibility.Visible),
                (Layer("CompactLayer").Visibility, Layer("WorkingLayer").Visibility, Layer("ConversationLayer").Visibility));
            Assert.Equal(1, Layer("ConversationLayer").Opacity);
            Assert.True(Layer("ConversationLayer").IsHitTestVisible);
            Assert.Equal(new Rect(96, 28, 418, 598), BoundsIn(host, Layer("ConversationLayer")));
            Assert.False(Named<SearchingIndicator>(window, "WorkingIndicator").IsActive);
            Assert.Equal(1, expanded);
            Assert.Equal("", bar.Query);
            Assert.Empty(placement.PointCalls);
            RenderGlass(window, "pill-open-rest.png", 2);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ThePillSaysWhatTheAssistantIsDoingAndWidensForLongerWords() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var (tracker, clock, activity) = CreateActivity();
        var assistant = CreateAssistant(answers: answers, frames: frames, activity: activity);
        var window = assistant.Window;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "when was it founded?";
            Assert.True(input.TrySubmit());
            Advance(frames[1], 500);
            Assert.True(window.IsWaitingForAnswer);
            Assert.Equal("Working", window.WorkingText);
            var working = GlassRegion(window).Width;

            // The model thinks: still "Working", and the pill does not move.
            using var thinking = tracker.Begin(ActivityKind.Model);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Pump();
            Assert.True(activity.IsVisible);
            Assert.Equal("Working", window.WorkingText);
            Assert.False(frames[1].Running);

            // It looks something up on the web: "Looking into it", and the pill widens to the longer words, easing, in 0.275 s.
            var looking = tracker.Begin(ActivityKind.WebSearch);
            Pump();
            Assert.Equal("Looking into it", window.WorkingText);
            Assert.True(frames[1].Running);
            var morph = frames[1];
            morph.Tick(morph.Last + TimeSpan.FromSeconds(1));
            var widths = new List<double> { GlassRegion(window).Width };
            while (morph.Running)
            {
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(5));
                widths.Add(GlassRegion(window).Width);
                Assert.InRange(widths.Count, 1, 200);
            }

            Assert.Equal(widths.Order(), widths);
            Assert.InRange((widths.Count - 1) * 5, 250, 300);
            var wide = GlassRegion(window).Width;
            Assert.True(wide > working + 30, "The pill did not widen for the longer words.");
            Assert.Equal(53, GlassRegion(window).Height, 3);
            Assert.True(window.IsWaitingForAnswer);

            // Back to thinking: "Working" again, and the pill narrows to it.
            looking.Dispose();
            Pump();
            Assert.Equal("Working", window.WorkingText);
            Advance(frames[1], 400);
            Assert.Equal(working, GlassRegion(window).Width, 3);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void EscapeOnThePillStopsTheAnswerAndPutsItAwayAndSoDoesAnAnswerThatEndsWithNothingToShow() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(answers: answers, frames: frames);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "first question";
            Assert.True(input.TrySubmit());
            Advance(frames[1], 500);
            Assert.True(window.IsWaitingForAnswer);
            Assert.True(conversation.IsAnswering);

            PressEscape(window);
            Assert.False(conversation.IsAnswering);
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);

            // It is the bar again the next time, empty, and the question is kept in the conversation with no answer.
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.False(window.IsWaitingForAnswer);
            Assert.Equal(CompactForm, window.Form);
            Assert.Equal("", bar.Query);
            Assert.Equal("first question", Assert.Single(conversation.Messages).Text);
            Assert.Equal(Visibility.Collapsed, GridNamed(window, "WorkingLayer").Visibility);

            // An answer that is stopped from elsewhere, with nothing to show, puts the pill away the same way.
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            input.Text = "second question";
            Assert.True(input.TrySubmit());
            Advance(frames[1], 500);
            Assert.True(window.IsWaitingForAnswer);
            conversation.Stop();
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AnAnswerThatComesWhileTheBarIsStillFoldingOpensThePanelFromWhereTheGlassIs() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(answers: answers, frames: frames);
        var window = assistant.Window;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "quick one";
            Assert.True(input.TrySubmit());
            frames[1].Tick(T0);
            frames[1].RunUntil(T0 + TimeSpan.FromMilliseconds(120));
            Assert.True(window.IsWaitingForAnswer);
            var folding = GlassRegion(window);
            Assert.InRange(folding.Width, 140, 440);

            answers.Show();

            // No jump: the panel opens from the glass as it stands, and ends as the panel.
            Assert.True(window.IsExpanding);
            Assert.Equal(folding.Width, GlassRegion(window).Width, 3);
            Advance(frames[1], 1000);
            Assert.False(window.IsExpanding);
            Assert.Equal(ConversationForm, window.Form);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void WithoutAnimationEffectsThePillAndThePanelAreThereAtOnce() => RunSta(() => WithTheme(() =>
    {
        var answers = new StreamingAnswers { Hold = true };
        var assistant = CreateAssistant(answers: answers, animations: false);
        var window = assistant.Window;
        try
        {
            assistant.Controller.Invoke();
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "no motion";
            Assert.True(input.TrySubmit());

            Assert.True(window.IsWaitingForAnswer);
            Assert.Equal(53, GlassRegion(window).Height, 3);
            Assert.Equal(1, GridNamed(window, "WorkingLayer").Opacity);

            answers.Show();
            Assert.False(window.IsWaitingForAnswer);
            Assert.False(window.IsExpanding);
            Assert.Equal(ConversationForm, window.Form);
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheShortcutPutsTheBarAwayAndOpensItAgainWithWhatWasTypedSelected() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Toggle();
            Advance(frames[0], 300);
            Assert.True(window.IsVisible);
            Assert.True(window.IsShowing);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "9+10";
            Assert.Equal("", input.SelectedText);

            // Pressed again: it leaves, and what was typed is kept.
            assistant.Controller.Toggle();
            Assert.False(window.IsShowing);
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            Assert.Equal("9+10", bar.Query);

            // And again: it is back with the text selected, so that typing replaces it and the right arrow carries on after it.
            assistant.Controller.Toggle();
            Advance(frames[0], 300);
            Assert.True(window.IsVisible);
            Assert.Equal("9+10", input.Text);
            Assert.Equal("9+10", input.SelectedText);

            // Pressed while it is on its way out, it turns back instead of going.
            assistant.Controller.Toggle();
            Assert.False(window.IsShowing);
            assistant.Controller.Toggle();
            Assert.True(window.IsShowing);
            Advance(frames[0], 300);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    }));
}
