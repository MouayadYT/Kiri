namespace Assistant.UI.Animation;

/// <summary>
/// How a floating surface appears and disappears: it fades in while it grows slightly and settles vertically into
/// place, and reverses that to leave. Declared as a theme token (Themes/Tokens/Motion.xaml).
/// </summary>
public sealed record SurfaceMotion
{
    /// <summary>Time to appear from fully hidden.</summary>
    public TimeSpan ShowDuration { get; init; } = TimeSpan.FromMilliseconds(240);

    /// <summary>Time to disappear from fully shown. Leaving is quicker than arriving.</summary>
    public TimeSpan HideDuration { get; init; } = TimeSpan.FromMilliseconds(160);

    /// <summary>The surface's scale, around its center, when hidden.</summary>
    public double HiddenScale { get; init; } = 0.96;

    /// <summary>The surface's vertical offset from its place when hidden, in DIPs. Negative is up.</summary>
    public double HiddenOffsetY { get; init; } = -8;

    /// <summary>How the surface looks at a presence between 0 (hidden) and 1 (shown).</summary>
    public SurfaceFrame FrameAt(double presence)
    {
        presence = Math.Clamp(presence, 0, 1);
        return new SurfaceFrame(
            presence,
            HiddenScale + ((1 - HiddenScale) * presence),
            HiddenOffsetY * (1 - presence));
    }
}

/// <summary>How a surface is drawn at one moment of a transition.</summary>
/// <param name="Opacity">Opacity, from 0 to 1.</param>
/// <param name="Scale">Scale around the surface's center.</param>
/// <param name="OffsetY">Vertical offset from its place, in DIPs.</param>
public readonly record struct SurfaceFrame(double Opacity, double Scale, double OffsetY);
