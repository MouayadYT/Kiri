using System.Windows;

namespace Assistant.UI.Voice;

/// <summary>
/// The shape of a voice visualizer's glow along the bottom edge of a surface, declared as a theme token
/// (Themes/Tokens/Voice.xaml). At rest it is one wide, low ellipse centered on the bottom edge. With voice energy
/// that ellipse grows upward, and swells rise from the edge beside it, drifting and breathing at their own paces so
/// the glow moves organically.
/// </summary>
public sealed record VoiceGlowShape
{
    // Each swell: how far its center drifts either side of the middle (a fraction of the width), and how fast; its
    // width (a fraction of the surface's); how fast it breathes; and phase offsets that keep the swells apart.
    private static readonly Swell[] Swells =
    [
        new(Drift: 0.26, DriftSpeed: 0.83, Width: 0.34, BreathSpeed: 2.3, Offset: 0.0),
        new(Drift: 0.30, DriftSpeed: 1.21, Width: 0.30, BreathSpeed: 3.1, Offset: 2.1),
        new(Drift: 0.22, DriftSpeed: 1.57, Width: 0.38, BreathSpeed: 2.7, Offset: 4.2),
    ];

    // How much of a swell's height comes and goes as it breathes.
    private const double Breath = 0.45;

    /// <summary>Vertical radius of the glow at rest, in DIPs.</summary>
    public double RestHeight { get; init; } = 110;

    /// <summary>Vertical radius of the main glow at full voice energy, in DIPs.</summary>
    public double PeakHeight { get; init; } = 280;

    /// <summary>Vertical radius of the tallest swell at full voice energy, in DIPs.</summary>
    public double SwellHeight { get; init; } = 240;

    /// <summary>Horizontal radius of the main glow, as a multiple of the surface's width.</summary>
    public double WidthRatio { get; init; } = 1.15;

    /// <summary>
    /// The ellipses that make up the glow on a surface of <paramref name="size"/>, at a voice
    /// <paramref name="energy"/> from 0 (at rest) to 1, and a motion <paramref name="phase"/> that advances over time.
    /// At rest the glow is a single ellipse that does not depend on the phase.
    /// </summary>
    public IReadOnlyList<GlowBlob> Layout(Size size, double energy, double phase)
    {
        energy = Math.Clamp(energy, 0, 1);
        var (width, bottom) = (size.Width, size.Height);
        var main = new GlowBlob(new Point(width / 2, bottom), width * WidthRatio, RestHeight + ((PeakHeight - RestHeight) * energy));
        if (energy <= 0)
        {
            return [main];
        }

        var blobs = new GlowBlob[Swells.Length + 1];
        blobs[0] = main;
        for (var i = 0; i < Swells.Length; i++)
        {
            var swell = Swells[i];
            var center = width * (0.5 + (swell.Drift * Math.Sin((phase * swell.DriftSpeed) + swell.Offset)));
            var breath = 0.5 + (0.5 * Math.Sin((phase * swell.BreathSpeed) + (swell.Offset * 1.7)));
            var radiusX = width * swell.Width * (1 + (0.15 * Math.Sin((phase * swell.BreathSpeed * 0.7) + swell.Offset)));
            var radiusY = SwellHeight * energy * (1 - Breath + (Breath * breath));
            blobs[i + 1] = new GlowBlob(new Point(center, bottom), radiusX, radiusY);
        }

        return blobs;
    }

    private readonly record struct Swell(double Drift, double DriftSpeed, double Width, double BreathSpeed, double Offset);
}

/// <summary>One soft ellipse of a voice glow, filled with the glow's radial gradient.</summary>
/// <param name="Center">Center of the ellipse, in DIPs from the surface's top-left corner.</param>
/// <param name="RadiusX">Horizontal radius, in DIPs.</param>
/// <param name="RadiusY">Vertical radius, in DIPs.</param>
public readonly record struct GlowBlob(Point Center, double RadiusX, double RadiusY);
