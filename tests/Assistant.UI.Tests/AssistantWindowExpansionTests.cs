using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private const string TypedQuestion = "  Typed exactly like this?  ";

    [Fact]
    public void TheBarGrowsIntoTheConversationInTheSameWindowFrameByFrame() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var placement = new FakePlacement();
        var assistant = CreateAssistant(placement, frames: frames);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var (compact, panel, host) = (Layer("CompactLayer"), Layer("ConversationLayer"), Layer("SurfaceHost"));
            Grid Layer(string name) => GridNamed(window, name);
            Assert.Equal(1, host.Opacity);

            // At rest it is the bar, alone: the pill of glass with its contents, in a window just big enough for it.
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(new Size(610, 183), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal(CompactForm, window.Form);
            Assert.Equal((Visibility.Visible, Visibility.Collapsed), (compact.Visibility, panel.Visibility));
            Assert.Equal(1, ShownAssistants());

            var input = Named<PromptInputControl>(window, "PromptInput");
            var transcript = Named<FadingScrollViewer>(window, "Transcript");
            input.Text = "Search or Ask about anything";
            RenderFixture(host, "window-compact.png", 2);
            input.Text = TypedQuestion;
            var activeBefore = window.IsActive;
            Assert.True(input.TrySubmit());

            // At once: the window is the conversation's, the question is the conversation's first message, and the
            // typed text is still in the pill, unchanged, as the glass has not yet begun to grow.
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.True(window.IsExpanding);
            Assert.Equal(new Size(610, 690), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal(TypedQuestion, bar.Query);
            Assert.Equal(TypedQuestion, input.Text);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(TypedQuestion, Assert.Single(conversation.Messages).Text);

            // Focus moved into the conversation without ever leaving the window.
            Assert.Same(transcript, FocusManager.GetFocusedElement(window));
            Assert.Equal(activeBefore, window.IsActive);
            if (window.IsActive)
            {
                Assert.True(transcript.IsKeyboardFocused);
            }

            // Frame by frame: one window, one glass hanging from the same top center, narrowing while it grows taller,
            // the typed text fading with it until the conversation's contents take its place.
            var morph = frames[1];
            morph.Tick(T0);
            RenderGlass(window, "morph-00.png", 2);
            var (width, height) = (520.0, 91.0);
            var (compactOpacity, conversationOpacity) = (1.0, 0.0);
            var steps = 0;
            while (window.IsExpanding)
            {
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(10));
                var glass = GlassRegion(window);
                Assert.Equal(1, ShownAssistants());
                Assert.Equal(305, glass.Left + (glass.Width / 2), 6);
                Assert.Equal(28, glass.Top, 6);
                Assert.InRange(glass.Width, 418 - 1e-6, width + 1e-6);
                Assert.InRange(glass.Height, height - 1e-6, 598 + 1e-6);
                (width, height) = (glass.Width, glass.Height);
                Assert.Equal(SurfaceForm.Blend(CompactForm, ConversationForm, window.ExpansionProgress), window.Form);
                Assert.Equal(window.Form.Width, glass.Width, 6);
                Assert.Equal(window.Form.Height, glass.Height, 6);

                Assert.True(compact.Opacity <= compactOpacity + 1e-9);
                Assert.True(panel.Opacity >= conversationOpacity - 1e-9);
                (compactOpacity, conversationOpacity) = (compact.Opacity, panel.Opacity);
                Assert.True(compactOpacity == 0 || conversationOpacity == 0, "Both states' contents show at once.");
                Assert.False(compact.IsHitTestVisible);
                Assert.Equal(compactOpacity > 0, compact.Visibility == Visibility.Visible);
                if (window.IsExpanding)
                {
                    Assert.Equal(TypedQuestion, input.Text);
                    Assert.Equal(TypedQuestion, bar.Query);
                }

                Assert.InRange(++steps, 1, 100);
                if (steps % 3 == 0 || !window.IsExpanding)
                {
                    RenderGlass(window, $"morph-{steps:D2}.png", 2);
                }
            }

            Assert.InRange(steps, 30, 50); // 400 ms in 10 ms frames.
            Assert.Equal(1, window.ExpansionProgress);
            Assert.Equal(ConversationForm, window.Form);
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));
            Assert.Equal((Visibility.Collapsed, Visibility.Visible), (compact.Visibility, panel.Visibility));
            Assert.Equal((0.0, 1.0), (compact.Opacity, panel.Opacity));
            Assert.True(panel.IsHitTestVisible);
            Assert.Equal(1, ShownAssistants());
            Backdrop.SetIsBlurred(window, false);
            RenderFixture(host, "window-conversation.png", 2);

            // The question is on show as the first bubble, as typed, the bar is empty for next time, and no placement
            // was needed: the window grew where it was.
            Assert.Equal("", bar.Query);
            Assert.Equal("", input.Text);
            var bubble = Assert.Single(Descendants<SpeechBubble>(panel));
            Assert.Equal(TypedQuestion, Descendants<TextBlock>(bubble).Single().Text);
            Assert.Same(transcript, FocusManager.GetFocusedElement(window));
            Assert.Empty(placement.PointCalls);
            Assert.Single(placement.Calls);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheBlurFollowsTheGlassAsItGrows() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var window = assistant.Window;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var backdrop = Assert.IsType<FakeBackdrop>(assistant.Backdrops.Created);
            var scale = PresentationSource.FromVisual(window)!.CompositionTarget.TransformToDevice.M11;

            void AssertBlurIsTheGlass()
            {
                var glass = GlassRegion(window);
                var radii = window.Form.GetBackdropCornerRadii(glass.Size);
                var expected = new Assistant.Windows.Backdrop.BackdropRegion(
                    glass.Left * scale, glass.Top * scale, glass.Width * scale, glass.Height * scale,
                    radii.Width * scale, radii.Height * scale);
                Assert.Equal(expected.X, backdrop.Region.X, 6);
                Assert.Equal(expected.Y, backdrop.Region.Y, 6);
                Assert.Equal(expected.Width, backdrop.Region.Width, 6);
                Assert.Equal(expected.Height, backdrop.Region.Height, 6);
                Assert.Equal(expected.CornerRadiusX, backdrop.Region.CornerRadiusX, 6);
                Assert.Equal(expected.CornerRadiusY, backdrop.Region.CornerRadiusY, 6);
            }

            AssertBlurIsTheGlass();
            Assert.Equal(520 * scale, backdrop.Region.Width, 6);

            Named<PromptInputControl>(window, "PromptInput").Text = "Synthetic question";
            Assert.True(Named<PromptInputControl>(window, "PromptInput").TrySubmit());
            var morph = frames[1];
            morph.Tick(T0);
            var widths = new List<double>();
            while (window.IsExpanding)
            {
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(10));
                AssertBlurIsTheGlass();
                widths.Add(backdrop.Region.Width);
            }

            Assert.Equal(widths.OrderDescending(), widths);
            Assert.Equal(418 * scale, backdrop.Region.Width, 6);
            Assert.Equal(598 * scale, backdrop.Region.Height, 6);
            Assert.Equal(PanelShape.GetBackdropCornerRadii(new Size(418, 598), 42.75).Width * scale, backdrop.Region.CornerRadiusX, 6);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AnotherAskWhileItGrowsAsksNothingAndTheHotkeyBringsTheSameWindowBack() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = TypedQuestion;
            Assert.True(input.TrySubmit());
            frames[1].Tick(T0);
            frames[1].RunUntil(T0 + TimeSpan.FromMilliseconds(100));
            Assert.True(window.IsExpanding);
            var handle = new WindowInteropHelper(window).Handle;
            var progress = window.ExpansionProgress;

            // Enter again, and the hotkey: neither starts another conversation, another window, or a restart.
            Assert.False(input.TrySubmit() && conversation.Messages.Count > 1);
            assistant.Controller.Invoke();
            Assert.Single(conversation.Messages);
            Assert.Equal(TypedQuestion, bar.Query);
            Assert.Equal(progress, window.ExpansionProgress);
            Assert.True(window.IsExpanding);
            Assert.Equal(1, ShownAssistants());
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            Assert.Same(Named<FadingScrollViewer>(window, "Transcript"), FocusManager.GetFocusedElement(window));
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void DismissingWhileItGrowsHidesTheWholeSurfaceAndTheNextInvocationIsTheBarAgain() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = TypedQuestion;
            Assert.True(input.TrySubmit());
            frames[1].Tick(T0);
            frames[1].RunUntil(T0 + TimeSpan.FromMilliseconds(100));
            Assert.True(window.IsExpanding);

            // Escape while it grows leaves the same way as from the panel, taking the growing glass with it.
            PressEscape(window);
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            Assert.False(frames[1].Running);
            Assert.False(window.IsExpanding);
            Assert.Equal(0, ShownAssistants());

            // It is the bar again, empty, and the conversation is kept.
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(183, GridNamed(window, "Root").Height);
            Assert.Equal(CompactForm, window.Form);
            Assert.Equal((Visibility.Visible, Visibility.Collapsed),
                (GridNamed(window, "CompactLayer").Visibility, GridNamed(window, "ConversationLayer").Visibility));
            Assert.Equal("", bar.Query);
            Assert.Equal(TypedQuestion, Assert.Single(conversation.Messages).Text);

            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Assert.True(window.IsVisible);
            Assert.Equal(new Size(610, 183), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window));
            Assert.Equal("", input.Text);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void WithoutAnimationEffectsTheBarBecomesTheConversationAtOnce() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant(animations: false);
        var (window, bar, conversation) = assistant;
        var expanded = 0;
        window.Expanded += (_, _) => expanded++;
        try
        {
            assistant.Controller.Invoke();
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = TypedQuestion;

            Assert.True(input.TrySubmit());

            Assert.Equal(1, expanded);
            Assert.False(window.IsExpanding);
            Assert.Equal(ConversationForm, window.Form);
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));
            Assert.Equal("", bar.Query);
            Assert.Equal(TypedQuestion, Assert.Single(conversation.Messages).Text);
            Assert.Equal(1, ShownAssistants());
            Assert.Same(Named<FadingScrollViewer>(window, "Transcript"), FocusManager.GetFocusedElement(window));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void VoiceInputHandsOverFromTheBarsGlowToThePanelsWhileItGrows() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            bar.Voice.Start();
            Assert.True(Named<VoiceGlow>(window, "BarGlow").IsActive);
            Named<PromptInputControl>(window, "PromptInput").Text = "Synthetic question";

            Assert.True(Named<PromptInputControl>(window, "PromptInput").TrySubmit());

            // The microphone stays on, now the panel's, and each state's glow follows its own voice input.
            Assert.False(bar.Voice.IsListening);
            Assert.True(conversation.Voice.IsListening);
            Assert.True(Assert.Single(assistant.BarMicrophone.Sessions).IsDisposed);
            Assert.False(Assert.Single(assistant.ConversationMicrophone.Sessions).IsDisposed);
            Assert.False(Named<VoiceGlow>(window, "BarGlow").IsActive);
            Assert.True(Named<VoiceGlow>(window, "PanelGlow").IsActive);
            Assert.True(Named<VoiceGlow>(window, "PanelGlow").IsVisible);

            // Both glows are clipped to the glass as it grows, so neither shows past it.
            frames[1].Tick(T0);
            frames[1].RunUntil(T0 + TimeSpan.FromMilliseconds(100));
            var clip = Named<Grid>(window, "GlowLayer").Clip.Bounds;
            Assert.Equal(window.Form.Width, clip.Width, 6);
            Assert.Equal(window.Form.Height, clip.Height, 6);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ThePanelSlidesUpAsItGrowsWhenItWouldNotFitBelowTheBar() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var placement = new FakePlacement
        {
            AnchorTop = new ScreenPoint(1000, 600),

            // No room for the tall panel below the pill: it needs the anchor 200 pixels higher.
            Fit = (layout, point) => layout.AnchorHeight > 100 ? new ScreenPoint(point.X, point.Y - 200) : point,
        };
        var assistant = CreateAssistant(placement, frames: frames);
        var window = assistant.Window;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Named<PromptInputControl>(window, "PromptInput").Text = "Synthetic question";
            Assert.True(Named<PromptInputControl>(window, "PromptInput").TrySubmit());

            // Nothing moves until the glass does; then the window rises a share of the way with every frame, placed by
            // the bar's own anchor, which is small enough to fit wherever it is going.
            Assert.Empty(placement.PointCalls);
            var morph = frames[1];
            morph.Tick(T0);
            var heights = new List<int>();
            while (window.IsExpanding)
            {
                var calls = placement.PointCalls.Count;
                morph.Tick(morph.Last + TimeSpan.FromMilliseconds(10));
                var (_, layout, point) = placement.PointCalls[^1];
                if (window.IsExpanding && placement.PointCalls.Count > calls)
                {
                    Assert.Equal(91, layout.AnchorHeight);
                    var share = (600 - point.Y) / 200.0;
                    Assert.InRange(Math.Abs(share - window.ExpansionProgress), 0, 0.006);
                }

                heights.Add(point.Y);
            }

            Assert.Equal(heights.OrderDescending(), heights);

            // It ends exactly where the panel fits, placed as the panel.
            var (_, finalLayout, finalPoint) = placement.PointCalls[^1];
            Assert.Equal((598.0, new ScreenPoint(1000, 400)), (finalLayout.AnchorHeight, finalPoint));
            Assert.All(placement.PointCalls.Take(placement.PointCalls.Count - 1), call => Assert.Equal(91.0, call.Layout.AnchorHeight));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ThePanelStaysPutWhenItFitsAndOnlyAPixelOfRoundingIsNotASlide() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var placement = new FakePlacement
        {
            AnchorTop = new ScreenPoint(1000, 600),
            Fit = (_, point) => new ScreenPoint(point.X + 1, point.Y - 1), // A rounding difference, not a move.
        };
        var assistant = CreateAssistant(placement, frames: frames);
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Named<PromptInputControl>(assistant.Window, "PromptInput").Text = "Synthetic question";
            Assert.True(Named<PromptInputControl>(assistant.Window, "PromptInput").TrySubmit());
            Advance(frames[1], 500);

            Assert.False(assistant.Window.IsExpanding);
            Assert.Empty(placement.PointCalls);
        }
        finally { assistant.Window.Close(); }
    }));

    [Fact]
    public void WithoutAnimationEffectsThePanelStillEndsWhereItFits() => RunSta(() => WithTheme(() =>
    {
        var placement = new FakePlacement
        {
            AnchorTop = new ScreenPoint(1000, 600),
            Fit = (layout, point) => layout.AnchorHeight > 100 ? new ScreenPoint(point.X, point.Y - 200) : point,
        };
        var assistant = CreateAssistant(placement, animations: false);
        try
        {
            assistant.Controller.Invoke();
            Named<PromptInputControl>(assistant.Window, "PromptInput").Text = "Synthetic question";
            Assert.True(Named<PromptInputControl>(assistant.Window, "PromptInput").TrySubmit());

            var (_, layout, point) = placement.PointCalls[^1];
            Assert.Equal((598.0, new ScreenPoint(1000, 400)), (layout.AnchorHeight, point));
        }
        finally { assistant.Window.Close(); }
    }));

    [Fact]
    public void AnEntryPointOtherThanTheBarShowsTheConversationAtOnceAndAVisibleBarGrowsIntoIt() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames);
        var (window, _, conversation) = assistant;
        conversation.StartNew("Synthetic question");
        try
        {
            window.ShowConversation();
            Advance(frames[0], 300);

            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.False(window.IsExpanding);
            Assert.Equal(new Size(610, 690), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));
            Assert.Equal((Visibility.Collapsed, Visibility.Visible),
                (GridNamed(window, "CompactLayer").Visibility, GridNamed(window, "ConversationLayer").Visibility));
            Assert.Same(Named<FadingScrollViewer>(window, "Transcript"), FocusManager.GetFocusedElement(window));

            // Dismissed, it comes back as the bar; and shown as the conversation while it is the bar, it grows.
            window.Dismiss();
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            window.ShowAndFocus();
            Advance(frames[0], 300);
            Assert.Equal(AssistantWindowState.Compact, window.State);
            window.ShowConversation();
            Assert.True(window.IsExpanding);
            Advance(frames[1], 500);
            Assert.Equal(ConversationForm, window.Form);
            Assert.Equal(1, ShownAssistants());
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void WithRealPlacementTheGrownPanelRisesToEndWhollyInsideTheWorkArea() => RunSta(() => WithTheme(() =>
    {
        // The real Win32 placement and the real rendering loop, on a window that is drawn fully transparent.
        var service = new WindowPlacementService(NullLogger<WindowPlacementService>.Instance);
        var assistant = CreateAssistant(service);
        var window = assistant.Window;
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var work = SystemParameters.WorkArea;
            var (left, right, bottom) = ((int)(work.Left * scale), (int)(work.Right * scale), (int)(work.Bottom * scale));

            // The bar low on the primary monitor, where the tall panel cannot fit below it.
            var low = new ScreenPoint((left + right) / 2, bottom - (int)(200 * scale));
            window.ShowAndFocus(low);
            Pump();
            var placed = Assert.NotNull(window.SurfaceTop);
            Assert.InRange(placed.X - low.X, -1, 1);
            Assert.InRange(placed.Y - low.Y, -1, 1);

            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "Synthetic question";
            Assert.True(input.TrySubmit());

            // Growing does not move the glass's top center at the start...
            var start = Assert.NotNull(window.SurfaceTop);
            Assert.InRange(start.X - low.X, -1, 1);
            Assert.InRange(start.Y - low.Y, -1, 1);

            // ...but by the end the window has risen, just far enough for the whole panel to be on screen.
            WaitUntil(() => !window.IsExpanding, "The bar did not finish growing.");
            var end = Assert.NotNull(window.SurfaceTop);
            Assert.InRange(end.X - low.X, -1, 1);
            Assert.True(end.Y < low.Y, "The panel did not rise to fit.");
            var panelBottom = end.Y + (598 * scale);
            Assert.InRange(panelBottom, bottom - 1.5, bottom + 1.5);
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.Equal(1, ShownAssistants());
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheRealBlurWindowFollowsTheGlassIntoThePanel() => RunSta(() => WithTheme(() =>
    {
        // The real composition backdrop: its own window, sized to the glass, must end up exactly under the panel.
        using var factory = new WindowBackdropFactory(NullLogger<WindowBackdropFactory>.Instance);
        var (bar, conversation) = (CreateBarModel(), CreateConversationModel());
        var window = new AssistantWindow(
            bar, conversation, factory, new WindowPlacementService(NullLogger<WindowPlacementService>.Instance))
        {
            Left = -10000, Top = -10000, Opacity = 0,
        };
        _ = new AssistantWindowStateController(window, bar, conversation);
        try
        {
            window.ShowAndFocus();
            WaitUntil(() => GridNamed(window, "SurfaceHost").Opacity == 1, "The bar did not finish showing.");
            var backdrop = new WindowInteropHelper(window).Owner;
            if (backdrop == 0 || !Backdrop.GetIsBlurred(window))
            {
                return; // Windows has no blur to give here; the glass is opaque and there is nothing to follow.
            }

            (double Left, double Top, double Width, double Height) Rects(bool glass)
            {
                GetWindowRect(glass ? new WindowInteropHelper(window).Handle : backdrop, out var rect);
                var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                var region = GlassRegion(window);
                return glass
                    ? (rect.Left + (region.Left * scale), rect.Top + (region.Top * scale), region.Width * scale, region.Height * scale)
                    : (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }

            void AssertBlurIsUnderTheGlass()
            {
                var (glass, blur) = (Rects(true), Rects(false));
                Assert.InRange(blur.Left - glass.Left, -2, 2);
                Assert.InRange(blur.Top - glass.Top, -2, 2);
                Assert.InRange(blur.Width - glass.Width, -2, 2);
                Assert.InRange(blur.Height - glass.Height, -2, 2);
            }

            AssertBlurIsUnderTheGlass();
            var pill = Rects(false);

            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "Synthetic question";
            Assert.True(input.TrySubmit());
            WaitUntil(() => !window.IsExpanding, "The bar did not finish growing.");
            Pump();

            AssertBlurIsUnderTheGlass();
            var panel = Rects(false);
            Assert.True(panel.Height > pill.Height * 5, "The blur did not grow with the glass.");
            Assert.True(panel.Width < pill.Width, "The blur did not narrow with the glass.");
        }
        finally { window.Close(); }
    }));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private static int ShownAssistants() =>
        Application.Current.Windows.OfType<AssistantWindow>().Count(window => window.IsVisible);

    // Runs a frame source for a while on a fresh clock, as the first frame only starts it.
    private static void Advance(FakeFrames frames, int milliseconds)
    {
        var start = frames.Last + TimeSpan.FromSeconds(1);
        frames.Tick(start);
        frames.RunUntil(start + TimeSpan.FromMilliseconds(milliseconds));
    }

    // The panel under the bar grows down out of it when it arrives (its height is the window's fifth frame loop, its fade and width the fourth):
    // this carries it to rest, for a test that looks at where things are once it has.
    private static void FinishPanel(List<FakeFrames> frames)
    {
        Advance(frames[4], 400);
        Advance(frames[3], 400);
    }
}
