using System.Windows;
using System.Windows.Controls;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Orb;
using Assistant.UI.Views;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The orb preview (the demo and test mode) -----------------------------------------------------------------

    private static OrbPreviewWindow CreateOrbPreview(FakeMicrophone microphone)
    {
        var window = new OrbPreviewWindow(microphone)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, Topmost = false,
        };
        window.Show();
        return window;
    }

    [Fact]
    public void AskingForTheDemoOrbOpensThePreviewAndItIsListedWithTheDemos() => RunSta(() =>
    {
        var preview = new RecordingOrbPreview();
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), orbPreview: preview);
        Assert.Contains("demo orb", demo.Answer("demo")!.Text);

        var answer = demo.Answer("Demo orb!");
        Assert.NotNull(answer);
        Assert.Contains("orb preview", answer.Text);
        Assert.Equal(1, preview.Shown);
        Assert.Equal(1, preview.Shown); // Asking anything else does not open it.
        demo.Answer("demo text");
        Assert.Equal(1, preview.Shown);

        // Without a preview to open, it says so and does nothing.
        var alone = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now));
        Assert.Contains("not available", alone.Answer("demo orb")!.Text);
    });

    [Fact]
    public void OrbPreviewFixtureRendersTheWindow() => RunSta(() =>
    {
        var window = CreateOrbPreview(new FakeMicrophone());
        try
        {
            window.UpdateLayout();
            RenderFixture((FrameworkElement)window.Content, "orb-preview-window.png", 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePreviewShowsTheOrbListeningToInventedSpeechAndChangesItsState() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var window = CreateOrbPreview(microphone);
        try
        {
            var orb = window.PreviewOrb;
            Assert.Equal(OrbState.Listening, orb.State);
            Assert.NotNull(orb.AmplitudeSource);
            Assert.Empty(microphone.Sessions); // Invented speech opens no microphone.
            Assert.All(Enumerable.Range(0, 200), _ => Assert.InRange(orb.AmplitudeSource!.ReadAmplitude(), 0, 1));

            foreach (var (name, state) in new[]
            {
                ("IdleButton", OrbState.Idle), ("ThinkingButton", OrbState.Thinking),
                ("ErrorButton", OrbState.Error), ("ListeningButton", OrbState.Listening),
            })
            {
                Assert.IsType<RadioButton>(window.FindName(name)).IsChecked = true;
                Assert.Equal(state, orb.State);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePreviewFeedsTheOrbFromASliderOrTheMicrophoneThroughItsOneInput() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var window = CreateOrbPreview(microphone);
        try
        {
            var source = window.PreviewOrb.AmplitudeSource!;
            var slider = Assert.IsType<Slider>(window.FindName("LevelSlider"));
            Assert.False(slider.IsEnabled);

            Assert.IsType<RadioButton>(window.FindName("SliderButton")).IsChecked = true;
            Assert.True(slider.IsEnabled);
            slider.Value = 0.3;
            Assert.Equal(0.3, source.ReadAmplitude(), 6);
            Assert.Empty(microphone.Sessions);

            // The real microphone's level, normalized, reaches the same orb input; it is on only while chosen.
            Assert.IsType<RadioButton>(window.FindName("MicrophoneButton")).IsChecked = true;
            Assert.False(slider.IsEnabled);
            Assert.NotNull(microphone.Current);
            microphone.Current!.Level = 0.05;
            Assert.InRange(source.ReadAmplitude(), 0.7, 0.95);
            microphone.Current.Level = 0;
            Assert.Equal(0, source.ReadAmplitude(), 6);

            Assert.IsType<RadioButton>(window.FindName("SliderButton")).IsChecked = true;
            Assert.Null(microphone.Current);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePreviewNeverLeavesTheMicrophoneOpen() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var window = CreateOrbPreview(microphone);
        Assert.IsType<RadioButton>(window.FindName("MicrophoneButton")).IsChecked = true;
        Assert.NotNull(microphone.Current);
        window.Close();
        Assert.Null(microphone.Current);
    });

    [Fact]
    public void WhenTheMicrophoneFailsThePreviewSaysWhyAndReturnsToInventedSpeech() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var window = CreateOrbPreview(microphone);
        try
        {
            var notice = Assert.IsType<TextBlock>(window.FindName("Notice"));
            var speech = Assert.IsType<RadioButton>(window.FindName("SpeechButton"));
            Assert.IsType<RadioButton>(window.FindName("MicrophoneButton")).IsChecked = true;
            microphone.Current!.Fail(MicrophoneFailure.AccessDenied);
            Pump();
            Assert.Equal("Microphone access is off in Settings", notice.Text);
            Assert.True(speech.IsChecked);
            Assert.Null(microphone.Current);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheLauncherOpensOnePreviewAtATimeAndAnotherOnceItIsClosed() => RunSta(() =>
    {
        var created = new List<OrbPreviewWindow>();
        var launcher = new OrbPreviewLauncher(() =>
        {
            var window = new OrbPreviewWindow(new FakeMicrophone())
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false, Topmost = false,
            };
            created.Add(window);
            return window;
        });
        try
        {
            launcher.Show();
            launcher.Show();
            Pump();
            Assert.Single(created);
            Assert.True(created[0].IsVisible);

            created[0].Close();
            launcher.Show();
            Pump();
            Assert.Equal(2, created.Count);
            Assert.True(created[1].IsVisible);
        }
        finally
        {
            foreach (var window in created.Where(window => window.IsVisible))
            {
                window.Close();
            }
        }
    });

    private sealed class RecordingOrbPreview : IOrbPreview
    {
        public int Shown { get; private set; }
        public void Show() => Shown++;
    }
}
