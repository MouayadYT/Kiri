using System.Windows;
using Assistant.UI.Voice;
using Xunit;

namespace Assistant.UI.Tests;

public sealed class VoiceVisualizerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);
    private static readonly Size Panel = new(418, 598);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(0.0001, 0)]      // -80 dB: a quiet room.
    [InlineData(0.00178, 0)]     // -55 dB: still nothing.
    [InlineData(0.01334, 0.5)]   // -37.5 dB: halfway.
    [InlineData(0.1, 1)]         // -20 dB: close, raised speech.
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    public void LoudnessIsEvenInDecibelsBetweenQuietAndLoud(double level, double expected) =>
        Assert.Equal(expected, VoiceEnergy.Loudness(level), 2);

    [Fact]
    public void EnergyRisesQuicklyWithSpeechAndSettlesSmoothlyToRest()
    {
        var energy = new VoiceEnergy();
        Run(energy, 0.0001, 1000);
        Assert.Equal(0, energy.Value);

        Run(energy, 0.1, 160);
        Assert.True(energy.Value > 0.9, $"Speech raised the energy only to {energy.Value:F2}.");

        Run(energy, 0, 96);
        var afterPause = energy.Value;
        Assert.InRange(afterPause, 0.6, 0.85); // Settling, not dropping.
        Run(energy, 0, 400);
        Assert.InRange(energy.Value, 0.1, afterPause);
        Run(energy, 0, 2400);
        Assert.Equal(0, energy.Value); // Back exactly at rest.

        Run(energy, 0.1, 160);
        energy.Reset();
        Assert.Equal(0, energy.Value);
    }

    [Fact]
    public void GlowRestsAsOneLowEllipseThatDoesNotMove()
    {
        var shape = new VoiceGlowShape();
        var rest = Assert.Single(shape.Layout(Panel, 0, 0));
        Assert.Equal(new GlowBlob(new Point(209, 598), 418 * 1.15, 110), rest);
        Assert.Equal(rest, Assert.Single(shape.Layout(Panel, 0, 7.3)));
    }

    [Fact]
    public void VoiceRaisesTheGlowWithSwellsThatDriftAndBreathe()
    {
        var shape = new VoiceGlowShape();
        double Top(double energy, double phase) => shape.Layout(Panel, energy, phase).Max(blob => blob.RadiusY);

        // Louder is taller, at any moment.
        foreach (var phase in new[] { 0.0, 1.3, 4.7 })
        {
            Assert.True(Top(0.3, phase) > Top(0, phase));
            Assert.True(Top(0.7, phase) > Top(0.3, phase));
            Assert.True(Top(1, phase) > Top(0.7, phase));
        }

        var full = shape.Layout(Panel, 1, 2);
        Assert.Equal(4, full.Count);
        Assert.Equal(280, full[0].RadiusY);
        foreach (var swell in full.Skip(1))
        {
            Assert.Equal(598, swell.Center.Y);
            Assert.InRange(swell.Center.X, 418 * 0.19, 418 * 0.81);
            Assert.InRange(swell.RadiusY, 240 * 0.55, 240);
        }

        // It moves on its own while there is voice.
        Assert.NotEqual(shape.Layout(Panel, 0.6, 1), shape.Layout(Panel, 0.6, 1.5));
        Assert.Equal(shape.Layout(Panel, 2, 1), shape.Layout(Panel, 1, 1)); // Energy is capped at 1.
    }

    private static void Run(VoiceEnergy energy, double level, int milliseconds)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += (int)Frame.TotalMilliseconds)
        {
            energy.Update(level, Frame);
        }
    }
}
