namespace Assistant.UI.Animation;

/// <summary>
/// How the Searching indicator's droplets circulate: they never move, and none of them turns around the center.
/// Each sits at a fixed place on the ring and only changes size, and the size emphasis travels from one droplet to the
/// next, which is what reads as rotation. Measured from the Searching reference, where six droplets stand 60° apart and
/// one stretch of the ring is largest, its sizes falling off gently ahead of it and steeply behind it.
/// A token, set in the theme (<c>Motion.SearchingRing</c>).
/// </summary>
/// <remarks>
/// The emphasis is a peak at a position along the ring, counted in droplets and moving clockwise. The peak has a flat
/// top half a droplet either side of its position, so the largest droplet, or two neighbors at a time, are at full
/// size; ahead of it the size eases down to the base over <see cref="AheadReach"/> droplets, behind it over
/// <see cref="BehindReach"/>. Every size is a smooth function of the peak's position, so the sizes change smoothly at
/// any frame rate.
/// </remarks>
public sealed record DropletRingMotion
{
    // Half the width of the peak's flat top, in droplets.
    private const double PeakHalfWidth = 0.5;

    /// <summary>How many droplets stand on the ring, evenly spaced.</summary>
    public int Count { get; init; } = 6;

    /// <summary>How long the emphasis takes to go once around the ring.</summary>
    public TimeSpan Lap { get; init; } = TimeSpan.FromMilliseconds(1300);

    /// <summary>A droplet's size when it is not emphasized, in device-independent pixels along its long axis.</summary>
    public double BaseLength { get; init; } = 3.4;

    /// <summary>A droplet's size at the peak, along its long axis.</summary>
    public double PeakLength { get; init; } = 6.5;

    /// <summary>Radius of the ring, from its center to the droplets' centers.</summary>
    public double RingRadius { get; init; } = 9.25;

    /// <summary>How much longer a droplet is than it is wide, at every size.</summary>
    public double Elongation { get; init; } = 1.16;

    /// <summary>Where on the ring the first droplet stands, clockwise from twelve o'clock, in degrees.</summary>
    public double FirstAngle { get; init; } = -8;

    /// <summary>The pose held while animation effects are off: where the peak stands, in droplets from the first.</summary>
    public double RestPeak { get; init; } = 4.5;

    /// <summary>How far ahead of the peak, in droplets, the size takes to ease down to the base.</summary>
    public double AheadReach { get; init; } = 2.5;

    /// <summary>How far behind the peak, in droplets, the size takes to drop to the base.</summary>
    public double BehindReach { get; init; } = 1.2;

    /// <summary>How far along its ring the emphasis is after <paramref name="elapsed"/>, in droplets from the first.</summary>
    public double PeakAt(TimeSpan elapsed) =>
        Mod(RestPeak + (elapsed / Lap * Count), Count);

    /// <summary>
    /// The share of the way from the base size to the peak size that droplet <paramref name="index"/> has when the
    /// emphasis stands at <paramref name="peak"/>, from 0 to 1.
    /// </summary>
    public double EmphasisOf(int index, double peak)
    {
        // How many droplets the droplet is ahead of the peak (positive, clockwise) or behind it, around the ring.
        var offset = Mod(index - peak + (Count / 2.0), Count) - (Count / 2.0);
        if (Math.Abs(offset) <= PeakHalfWidth)
        {
            return 1;
        }

        var (distance, reach) = offset > 0
            ? (offset - PeakHalfWidth, AheadReach)
            : (-offset - PeakHalfWidth, BehindReach);
        if (distance >= reach)
        {
            return 0;
        }

        // Behind the peak the size drops steeply along a cosine; ahead of it, it dies away in a longer, gentler curve.
        // The two meet at the far side of the ring, where both are the base size, so the sizes are continuous all round.
        return offset > 0 ? Math.Pow(1 - (distance / reach), 1.25) : 0.5 * (1 + Math.Cos(Math.PI * distance / reach));
    }

    /// <summary>The length of droplet <paramref name="index"/> along its long axis, when the emphasis stands at <paramref name="peak"/>.</summary>
    public double LengthOf(int index, double peak) =>
        BaseLength + ((PeakLength - BaseLength) * EmphasisOf(index, peak));

    /// <summary>Where droplet <paramref name="index"/> stands, in degrees clockwise from twelve o'clock.</summary>
    public double AngleOf(int index) => FirstAngle + (360.0 * index / Count);

    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
