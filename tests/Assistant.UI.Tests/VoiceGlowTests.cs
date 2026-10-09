using System.Windows;
using System.Windows.Media;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void GlowStaysGoneAndStillUntilVoiceInputStarts() => RunSta(() =>
    {
        var (window, glow, frames, source) = CreateGlowWindow();
        try
        {
            window.Show();
            Assert.Equal(0, glow.Presence);
            Assert.False(frames.Running);
            Assert.False(glow.IsAnimating);
            Assert.Equal(0, source.Reads);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowRisesRestsFollowsTheVoiceSettlesAndSinksAway() => RunSta(() =>
    {
        var (window, glow, frames, source) = CreateGlowWindow();
        try
        {
            window.Show();
            glow.IsActive = true;
            Assert.True(frames.Running);
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(160));
            Assert.InRange(glow.Presence, 0.5, 0.99); // Rising into view.
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(320));
            Assert.Equal(1, glow.Presence);

            // Quiet: it rests low, and keeps listening.
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(1000));
            Assert.Equal(0, glow.Energy);
            Assert.True(frames.Running);
            Assert.True(source.Reads > 50);

            // Speech pushes it up; silence lets it settle back to rest.
            source.Level = 0.05;
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(1200));
            Assert.True(glow.Energy > 0.7, $"Speech raised the glow only to {glow.Energy:F2}.");
            var moving = glow.Phase;
            source.Level = 0;
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(1300));
            Assert.InRange(glow.Energy, 0.4, 0.8);
            Assert.True(glow.Phase > moving);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(4000));
            Assert.Equal(0, glow.Energy);
            Assert.Equal(1, glow.Presence);

            // Voice input ends: it sinks away, then stops running.
            var reads = source.Reads;
            glow.IsActive = false;
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(4200));
            Assert.InRange(glow.Presence, 0.01, 0.5);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(4500));
            Assert.Equal(0, glow.Presence);
            Assert.False(frames.Running);
            Assert.Equal(reads, source.Reads); // The microphone is not read once voice input is off.
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HiddenGlowStopsAtOnceAndRisesAfreshWhenShownAgain() => RunSta(() =>
    {
        var (window, glow, frames, source) = CreateGlowWindow();
        try
        {
            window.Show();
            glow.IsActive = true;
            source.Level = 0.1;
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(500));
            Assert.True(glow.Energy > 0);

            window.Hide();
            Assert.False(frames.Running);
            Assert.Equal(0, glow.Presence);
            Assert.Equal(0, glow.Energy);

            window.Show();
            Assert.True(frames.Running);
            Assert.Equal(0, glow.Presence);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithoutAnimationEffectsGlowShowsAtRestWithoutMoving() => RunSta(() =>
    {
        var (window, glow, frames, source) = CreateGlowWindow(animations: false);
        try
        {
            window.Show();
            source.Level = 0.1;
            glow.IsActive = true;
            Assert.Equal(1, glow.Presence);
            Assert.Equal(0, glow.Energy);
            Assert.False(frames.Running);
            glow.IsActive = false;
            Assert.Equal(0, glow.Presence);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowTokensAndBrushComeFromTheTheme() => RunSta(() =>
    {
        var theme = new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) };
        var panel = Assert.IsType<VoiceGlowShape>(theme["VoiceGlow.Panel"]);
        var bar = Assert.IsType<VoiceGlowShape>(theme["VoiceGlow.Bar"]);
        Assert.Equal(new VoiceGlowShape { RestHeight = 110, PeakHeight = 280, SwellHeight = 240, WidthRatio = 1.15 }, panel);
        Assert.True(bar.PeakHeight < 91 * 0.7, "The bar's glow must leave its text readable.");
        var brush = Assert.IsType<RadialGradientBrush>(theme["Brush.VoiceGlow"]);
        Assert.Equal(Colors.White, brush.GradientStops[0].Color);
        Assert.Equal(0, brush.GradientStops[^1].Color.A);
    });

    [Fact]
    public void VoiceInputOpensTheMicrophoneOnlyWhileListening() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var voice = new VoiceInputViewModel(microphone);
        var changes = new List<string?>();
        voice.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(voice.IsListening);
        Assert.Equal(0, voice.ReadLevel());

        voice.ToggleCommand.Execute(null);
        Assert.True(voice.IsListening);
        Assert.Contains(nameof(VoiceInputViewModel.IsListening), changes);
        var session = Assert.Single(microphone.Sessions);
        session.Level = 0.25;
        Assert.Equal(0.25, voice.ReadLevel());
        voice.Start();
        Assert.Single(microphone.Sessions); // Already listening.

        voice.ToggleCommand.Execute(null);
        Assert.False(voice.IsListening);
        Assert.True(session.IsDisposed);
        Assert.Equal(0, voice.ReadLevel());
    });

    [Fact]
    public void MicrophoneFailureStopsListeningAndSaysWhy() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var voice = new VoiceInputViewModel(microphone);
        voice.Start();
        var first = microphone.Current!;

        first.Fail(MicrophoneFailure.AccessDenied);
        Pump();
        Assert.False(voice.IsListening);
        Assert.True(first.IsDisposed);
        Assert.Equal("Microphone access is off in Settings", voice.FailureMessage);

        // Starting again clears the message; a late failure from the old session changes nothing.
        voice.Start();
        Assert.Null(voice.FailureMessage);
        first.Fail(MicrophoneFailure.Disconnected);
        Pump();
        Assert.True(voice.IsListening);
        Assert.Null(voice.FailureMessage);

        microphone.Current!.Fail(MicrophoneFailure.NoMicrophone);
        Pump();
        Assert.Equal("No microphone found", voice.FailureMessage);
        voice.Stop();
        Assert.Null(voice.FailureMessage);
    });

    [Fact]
    public void BarEscapeStopsVoiceThenClearsThenDismissesAndAskHandsOverTheQuestion() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var bar = CreateBarModel(microphone);
        var asked = new List<string>();
        bar.AskRequested += (_, question) => asked.Add(question);

        Assert.False(bar.AskCommand.CanExecute(null));
        bar.Query = "   ";
        Assert.False(bar.AskCommand.CanExecute(null));
        bar.Query = " What is due today? ";
        Assert.True(bar.AskCommand.CanExecute(null));

        bar.Voice.Start();
        Assert.False(bar.HandleEscape());
        Assert.False(bar.Voice.IsListening);
        Assert.Equal(" What is due today? ", bar.Query);

        bar.AskCommand.Execute(null);
        Assert.Equal(new[] { " What is due today? " }, asked);
        Assert.Equal(" What is due today? ", bar.Query); // Still on screen, until the bar has grown into the answer.
        bar.Query = "";
        Assert.True(bar.HandleEscape());

        Assert.Equal("Search or Ask", bar.Placeholder);
        bar.Voice.Start();
        microphone.Current!.Fail(MicrophoneFailure.NoMicrophone);
        Pump();
        Assert.Equal("No microphone found", bar.Placeholder);
        bar.Voice.Stop();
        Assert.Equal("Search or Ask", bar.Placeholder);
    });

    private static (Window Window, VoiceGlow Glow, FakeFrames Frames, FakeLevelSource Source) CreateGlowWindow(bool animations = true)
    {
        var frames = new FakeFrames();
        var source = new FakeLevelSource();
        var glow = new VoiceGlow(frames, () => animations)
        {
            Width = 418, Height = 598, Source = source, Shape = new VoiceGlowShape(), Fill = Brushes.White,
        };
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, Content = glow,
        };
        return (window, glow, frames, source);
    }

    private sealed class FakeLevelSource : IVoiceLevelSource
    {
        public double Level { get; set; }
        public int Reads { get; private set; }
        public double ReadLevel() { Reads++; return Level; }
    }
}
