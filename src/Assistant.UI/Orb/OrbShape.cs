namespace Assistant.UI.Orb;

/// <summary>
/// The shape of the assistant orb's bright boundary at rest and how it answers sound, declared as a theme token
/// (Themes/Tokens/Orb.xaml). Every length is a fraction of the orb's radius, measured downward from its center, so
/// the orb looks the same at any size.
/// </summary>
/// <remarks>
/// At rest the boundary is the reference's: a shallow smile across the orb's middle, low at the center and rising to
/// meet the rim on both sides. Sound lifts it and sets it rippling.
/// </remarks>
public sealed record OrbShape
{
    // ---- At rest, measured from the reference (2x image, orb 206 px across) --------------------------------------

    /// <summary>How far below the orb's center the boundary is at its flanks, where it is highest.</summary>
    public double RestFloor { get; init; } = 0.05;

    /// <summary>How much lower the smile's middle is than its flanks.</summary>
    public double DipDepth { get; init; } = 0.215;

    /// <summary>Where the lowest point of the smile is, from the orb's middle (positive is right).</summary>
    public double DipCenter { get; init; } = 0.0;

    /// <summary>Half-width of the smile, either side of its lowest point.</summary>
    public double DipWidth { get; init; } = 0.43;

    /// <summary>How far the boundary sinks toward the rim at both ends, where the sphere curves away.</summary>
    public double EdgeDrop { get; init; } = 0.05;

    /// <summary>How thick the bright band above the boundary is at its middle at rest.</summary>
    public double CoreThickness { get; init; } = 0.16;

    // ---- How sound moves it ---------------------------------------------------------------------------------------

    /// <summary>How far the whole boundary rises at full loudness.</summary>
    public double Lift { get; init; } = 0.30;

    /// <summary>How tall the ripples running along the boundary are at full loudness.</summary>
    public double Ripple { get; init; } = 0.10;

    /// <summary>How tall a swell thrown up by the start of a sound is, at full loudness.</summary>
    public double Swell { get; init; } = 0.15;

    /// <summary>How far the smile's lowest point slides sideways at full loudness.</summary>
    public double Sway { get; init; } = 0.30;

    /// <summary>How much brighter and thicker the bright band grows at full loudness (0.9 is nearly twice).</summary>
    public double Bloom { get; init; } = 0.9;

    /// <summary>How much faster the ripples travel at full loudness (1.4 is two and a half times).</summary>
    public double Quickening { get; init; } = 1.4;

    /// <summary>Loudness at or under which nothing shows: the room's noise.</summary>
    public double Gate { get; init; } = 0.015;

    /// <summary>The exponent that lifts quiet speech, so it shows without loud speech saturating (1 is none).</summary>
    public double Compression { get; init; } = 0.7;

    /// <summary>How quickly the boundary follows louder sound, in milliseconds.</summary>
    public double AttackMs { get; init; } = 55;

    /// <summary>How quickly the boundary settles when the sound gets quieter, in milliseconds.</summary>
    public double ReleaseMs { get; init; } = 320;

    // ---- The other states -----------------------------------------------------------------------------------------

    /// <summary>The height of the slow wave that passes along the boundary while thinking.</summary>
    public double ThinkingWave { get; init; } = 0.03;

    /// <summary>How long the thinking wave takes to cross the orb, in seconds.</summary>
    public double ThinkingPeriod { get; init; } = 2.4;

    /// <summary>How far the boundary sags when there is an error, and how much its smile flattens (0 to 1).</summary>
    public double ErrorSag { get; init; } = 0.05;

    /// <summary>How far the orb's light warms to the error color, from 0 to 1.</summary>
    public double ErrorTint { get; init; } = 0.62;

    /// <summary>How far the boundary moves as the orb breathes at idle.</summary>
    public double Breath { get; init; } = 0.006;

    // ---- The boundary itself --------------------------------------------------------------------------------------

    /// <summary>
    /// The boundary's height at horizontal position <paramref name="x"/> (from -1 at the orb's left edge to 1 at its
    /// right) at rest: its distance below the orb's center, in radii. <paramref name="dipScale"/> deepens or flattens
    /// the smile (1 is the reference's), and <paramref name="shift"/> slides it sideways.
    /// </summary>
    public double RestBoundary(double x, double dipScale = 1, double shift = 0)
    {
        var center = DipCenter + shift;
        var offset = x - center;
        var dip = DipDepth * dipScale * Math.Exp(-(offset / DipWidth) * (offset / DipWidth));
        return RestFloor + dip + (EdgeDrop * Math.Pow(Math.Abs(x), 6));
    }
}
