using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Assistant.UI.Controls;
using Assistant.UI.Orb;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The orb: fixtures ---------------------------------------------------------------------------------------

    // The reference is a 2x image, 606 x 374 px, with the orb 206 px across, centered at (249, 167) px.
    private static (Window Window, Grid Host, AssistantOrb Orb, FakeFrames Frames) CreateOrbWindow(
        bool animations = true, double side = 103)
    {
        var frames = new FakeFrames();
        var orb = new AssistantOrb(frames, () => animations)
        {
            Width = side, Height = side, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(124.5 - (side / 2), 83.5 - (side / 2), 0, 0),
        };
        var host = new Grid { Width = 303, Height = 187, Background = Brushes.White, Children = { orb } };
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, Content = host,
        };
        return (window, host, orb, frames);
    }

    [Fact]
    public void OrbFixturesRenderEachStateInTheReferencePose() => RunSta(() =>
    {
        foreach (var state in Enum.GetValues<OrbState>())
        {
            var (window, host, orb, _) = CreateOrbWindow(animations: false);
            try
            {
                window.Show();
                orb.State = state;
                host.UpdateLayout();
                RenderFixture(host, $"orb-{state.ToString().ToLowerInvariant()}-2x.png", 2);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void OrbFixturesRenderListeningAtEachLoudness() => RunSta(() =>
    {
        foreach (var amplitude in new[] { 0.0, 0.12, 0.35, 0.7, 1.0 })
        {
            var (window, host, orb, frames) = CreateOrbWindow();
            try
            {
                window.Show();
                orb.State = OrbState.Listening;
                orb.Amplitude = amplitude;
                frames.Tick(T0);
                frames.RunUntil(T0 + TimeSpan.FromSeconds(1.7));
                host.UpdateLayout();
                RenderFixture(host, $"orb-listening-{(int)(amplitude * 100):D3}-2x.png", 2);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void OrbFixturesRenderMockSpeechOverTime() => RunSta(() =>
    {
        var (window, host, orb, frames) = CreateOrbWindow();
        try
        {
            window.Show();
            var speech = new MockSpeechAmplitude(seed: 7);
            orb.State = OrbState.Listening;
            frames.Tick(T0);
            for (var i = 0; i < 24; i++)
            {
                // Each picture is a quarter of a second on.
                var until = T0 + TimeSpan.FromSeconds(0.25 * (i + 1));
                while (frames.Last < until)
                {
                    orb.Amplitude = speech.At((frames.Last - T0).TotalSeconds);
                    frames.Tick(frames.Last + TimeSpan.FromMilliseconds(16));
                }

                host.UpdateLayout();
                RenderFixture(host, $"orb-speech-{i:D2}-2x.png", 2);
            }
        }
        finally { window.Close(); }
    });
}
