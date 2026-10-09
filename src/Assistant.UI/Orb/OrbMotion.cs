namespace Assistant.UI.Orb;

/// <summary>
/// What the orb's light looks like at this moment, apart from where its boundary runs.
/// </summary>
/// <param name="Glow">How bright the light along the boundary is; 1 is the reference's.</param>
/// <param name="Thickness">How thick the bright band is, as a multiple of the reference's.</param>
/// <param name="Sheen">Where a soft glint sits along the boundary, from -1 to 1, or <see langword="null"/> for none.</param>
/// <param name="SheenStrength">How strong the glint is, from 0 to 1.</param>
/// <param name="Tint">How far the light has warmed to the error color, from 0 to 1.</param>
/// <param name="Lens">How much the glass under the bright band brightens, as a multiple of the reference's.</param>
internal readonly record struct OrbPose(double Glow, double Thickness, double Sheen, double SheenStrength, double Tint, double Lens);

/// <summary>
/// The orb's living motion: turns the loudness the orb is given, and the state it is in, into where its bright
/// boundary runs and how brightly it glows. It keeps no clock of its own: the caller updates it frame by frame.
/// </summary>
/// <remarks>
/// Listening, the loudness is gated (the room's noise shows nothing), lifted (quiet speech still shows) and then
/// followed by two smoothers, a quick one that lifts the boundary and a slower one that swells the light, so the
/// motion is fluid and never jitters. The boundary is the sum of a lift, three ripples of unrelated pace whose spacing
/// wanders, and a swell thrown up wherever the sound starts, which travels along the boundary and dies away. The
/// ripples' height follows the loudness, and everything is zero when the loudness is, so the boundary always
/// settles into the reference's shape, give or take the faint breathing. Nothing repeats: the ripples never line up twice, and a swell
/// appears wherever a sound happens to start.
/// </remarks>
internal sealed class OrbMotion
{
    // Each ripple: how many waves fit across the orb, how fast it travels, its share of the ripple height, and where it starts.
    private static readonly (double Waves, double Speed, double Share, double Offset)[] Ripples =
    [
        (0.55, 2.1, 1.00, 0.0),
        (0.95, -3.3, 0.62, 1.9),
        (1.60, 4.7, 0.36, 4.1),
    ];

    // A sound starting this much louder than the orb shows throws up a swell, and at most one comes each 90 ms.
    private const double OnsetStep = 0.10;
    private const double SwellSpacing = 0.09;
    private const double SwellLife = 0.55;
    private const int MaxSwells = 6;

    // Below this the quick smoother is treated as settled.
    private const double Rest = 0.002;

    private readonly OrbShape _shape;
    private readonly Random _random;
    private readonly List<Swell> _swells = [];
    private double _spacingLeft;
    private double _clock;
    private double _phase;
    private double _errorAge = double.PositiveInfinity;
    private OrbState _state;

    public OrbMotion(OrbShape shape, int seed = 20260929)
    {
        _shape = shape;
        _random = new Random(seed);
    }

    /// <summary>How loud the orb shows the sound, from 0 to 1, following quickly.</summary>
    public double Level { get; private set; }

    /// <summary>How loud the orb shows the sound, from 0 to 1, following slowly.</summary>
    public double Envelope { get; private set; }

    /// <summary>How far the orb is in the listening state, from 0 to 1.</summary>
    public double Listening { get; private set; }

    /// <summary>How far the orb is in the thinking state, from 0 to 1.</summary>
    public double Thinking { get; private set; }

    /// <summary>How far the orb is in the error state, from 0 to 1.</summary>
    public double Error { get; private set; }

    /// <summary>How many swells are travelling along the boundary.</summary>
    public int SwellCount => _swells.Count;

    /// <summary>Whether the sound is moving the orb: it is not listening quietly, and nothing is left to settle.</summary>
    public bool IsReacting => Level > 0 || Envelope > 0 || _swells.Count > 0 || _errorAge < 1.6;

    /// <summary>How the orb's light looks now.</summary>
    public OrbPose Pose
    {
        get
        {
            var shape = _shape;
            var beat = Math.Sin(_clock * Math.Tau / shape.ThinkingPeriod * 1.3);
            var glow = 1 + (shape.Bloom * 0.55 * Envelope) + (0.10 * Thinking * beat) + (0.03 * Math.Sin(_clock * Math.Tau / 6.5));
            var thickness = 1 + (shape.Bloom * 0.8 * Envelope) + (0.10 * Level) + (0.10 * Thinking * beat);

            // While thinking, a soft glint travels back and forth along the boundary.
            var sheen = 0.62 * Math.Sin(_clock * Math.Tau / (shape.ThinkingPeriod * 1.5));
            return new OrbPose(
                Glow: glow,
                Thickness: thickness,
                Sheen: sheen,
                SheenStrength: Thinking * (1 - Error),
                Tint: Error * shape.ErrorTint,
                Lens: 1 + (0.9 * Envelope) + (0.15 * Thinking * beat));
        }
    }

    /// <summary>Returns to the reference's pose at once, ready to start afresh.</summary>
    public void Reset()
    {
        (Level, Envelope, Listening, Thinking, Error) = (0, 0, 0, 0, 0);
        _swells.Clear();
        _spacingLeft = 0;
        _errorAge = double.PositiveInfinity;
        _state = OrbState.Idle;
    }

    /// <summary>Rests in <paramref name="state"/> at once, in the reference's pose for it, without moving on.</summary>
    public void Snap(OrbState state)
    {
        Reset();
        _state = state;
        (Listening, Thinking, Error) = (state == OrbState.Listening ? 1 : 0, state == OrbState.Thinking ? 1 : 0, state == OrbState.Error ? 1 : 0);
    }

    /// <summary>
    /// Moves on by <paramref name="elapsed"/>, in <paramref name="state"/>, with sound of loudness
    /// <paramref name="amplitude"/> from 0 to 1. Only a listening orb hears the sound.
    /// </summary>
    public void Update(double amplitude, OrbState state, TimeSpan elapsed)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        if (state == OrbState.Error && _state != OrbState.Error)
        {
            _errorAge = 0;
        }

        _state = state;
        Listening = Follow(Listening, state == OrbState.Listening ? 1 : 0, seconds, 0.15);
        Thinking = Follow(Thinking, state == OrbState.Thinking ? 1 : 0, seconds, 0.35);
        Error = Follow(Error, state == OrbState.Error ? 1 : 0, seconds, 0.22);
        _errorAge = state == OrbState.Error ? _errorAge + seconds : double.PositiveInfinity;

        var target = state == OrbState.Listening ? Shaped(amplitude) : 0;
        if (target - Level > OnsetStep && _spacingLeft <= 0 && _swells.Count < MaxSwells)
        {
            Throw(target);
            _spacingLeft = SwellSpacing;
        }

        _spacingLeft -= seconds;
        Level = Follow(Level, target, seconds, target > Level ? _shape.AttackMs / 1000 : _shape.ReleaseMs / 1000);
        Envelope = Follow(Envelope, target, seconds, target > Envelope ? 0.22 : 0.60);
        if (target == 0)
        {
            Level = Level < Rest ? 0 : Level;
            Envelope = Envelope < Rest ? 0 : Envelope;
        }

        _clock += seconds;

        // Louder sound carries the ripples along faster.
        _phase += seconds * (1 + (_shape.Quickening * Level));
        for (var i = _swells.Count - 1; i >= 0; i--)
        {
            var swell = _swells[i] with { Age = _swells[i].Age + seconds };
            if (swell.Age > SwellLife * 4)
            {
                _swells.RemoveAt(i);
            }
            else
            {
                _swells[i] = swell;
            }
        }
    }

    /// <summary>
    /// The boundary's height at horizontal position <paramref name="x"/> (from -1 at the orb's left edge to 1 at its
    /// right): its distance below the orb's center, in radii.
    /// </summary>
    public double BoundaryAt(double x)
    {
        var shape = _shape;

        // The smile slides sideways with the slow loudness; an error flattens it and lets it sag.
        var shift = shape.Sway * Envelope * Math.Sin((_phase * 0.8) + 1.3);
        var dipScale = 1 - (0.65 * Error) + (0.10 * Thinking * Math.Sin(_clock * Math.Tau / (shape.ThinkingPeriod * 1.3)));
        var y = shape.RestBoundary(x, dipScale, shift) + (shape.ErrorSag * Error);

        var edge = Edge(x);
        var up = shape.Lift * Level * (0.65 + (0.35 * (1 - (x * x))));
        var loudness = Math.Pow(Level, 1.15);
        foreach (var (waves, speed, share, offset) in Ripples)
        {
            // The spacing wanders slowly, so the ripples never line up the same way twice.
            var spread = 1 + (0.18 * Math.Sin((0.37 * _phase) + offset));
            up += shape.Ripple * share * loudness * edge * Math.Sin((Math.Tau * waves * spread * x) - (speed * _phase) + offset);
        }

        foreach (var swell in _swells)
        {
            var travelled = swell.Origin + (swell.Direction * 0.55 * swell.Age);
            var width = 0.22 + (0.25 * swell.Age);
            var offset = (x - travelled) / width;
            var rise = 1 - Math.Exp(-swell.Age / 0.06);
            up += swell.Sign * shape.Swell * swell.Strength * rise * Math.Exp(-swell.Age / SwellLife) * Math.Exp(-(offset * offset)) * edge;
        }

        // While thinking, a slow wave passes along the boundary, whatever the sound.
        up += shape.ThinkingWave * Thinking * edge
            * Math.Sin((Math.Tau * 0.5 * x) - (Math.Tau * _clock / shape.ThinkingPeriod));

        // A moment of unrest as an error arrives, dying away as the orb sags and holds still.
        if (_errorAge < 1.6)
        {
            up += 0.045 * Math.Exp(-_errorAge / 0.42) * Math.Sin(Math.Tau * 5 * _errorAge) * (1 - (x * x));
        }

        // At rest the orb still breathes, very slightly.
        up += shape.Breath * Math.Sin(_clock * Math.Tau / 7) * (1 - (0.5 * x * x));
        return y - up;
    }

    // The bright band's thickness at x: as thick as the reference's at the middle of the smile, about half as thick
    // where the smile has risen half way, and a fine line at the flanks.
    public double ThicknessAt(double x, OrbPose pose)
    {
        var offset = Math.Abs(x - _shape.DipCenter) / 0.45;
        return _shape.CoreThickness * pose.Thickness * (0.02 + (0.98 * Math.Exp(-0.7 * Math.Pow(offset, 2.6))));
    }

    // Loudness from 0 to 1, with the room's noise gated out and quiet speech lifted.
    private double Shaped(double amplitude)
    {
        if (!(amplitude > _shape.Gate))
        {
            return 0;
        }

        var scaled = Math.Min(1, (amplitude - _shape.Gate) / (1 - _shape.Gate));
        return Math.Pow(scaled, _shape.Compression);
    }

    private void Throw(double strength)
    {
        _swells.Add(new Swell(
            Origin: (_random.NextDouble() * 1.1) - 0.55,
            Direction: _random.NextDouble() < 0.5 ? -1 : 1,
            Sign: _random.NextDouble() < 0.7 ? 1 : -1,
            Strength: Math.Clamp(strength, 0.2, 1),
            Age: 0));
    }

    // The boundary's rise fades to nothing at the orb's edge, where it meets the rim.
    private static double Edge(double x)
    {
        var t = Math.Clamp((Math.Abs(x) - 0.72) / 0.28, 0, 1);
        return 1 - (t * t * (3 - (2 * t)));
    }

    private static double Follow(double value, double target, double seconds, double timeConstant) =>
        timeConstant <= 0 ? target : value + ((target - value) * (1 - Math.Exp(-seconds / timeConstant)));

    private readonly record struct Swell(double Origin, double Direction, double Sign, double Strength, double Age);
}
