using Assistant.UI.Orb;
using Xunit;

namespace Assistant.UI.Tests;

public sealed class OrbMotionTests
{
    private static readonly OrbShape Shape = new();
    private static readonly double[] Grid = [.. Enumerable.Range(-20, 41).Select(i => i / 20.0)];

    // Runs the motion at 60 frames a second, with the sound loudness as a function of time.
    private static void Run(OrbMotion motion, OrbState state, double seconds, Func<double, double> amplitude, Action<double>? each = null)
    {
        var step = TimeSpan.FromSeconds(1.0 / 60);
        for (var t = 0.0; t < seconds; t += 1.0 / 60)
        {
            motion.Update(amplitude(t), state, step);
            each?.Invoke(t);
        }
    }

    // How far the boundary is from its shape at rest, at the place it is furthest.
    private static double Displacement(OrbMotion motion) => Grid.Max(x => Math.Abs(motion.BoundaryAt(x) - Shape.RestBoundary(x)));

    // The largest displacement over the last second of a run at a constant loudness.
    private static double PeakDisplacement(double amplitude)
    {
        var motion = new OrbMotion(Shape);
        var peak = 0.0;
        Run(motion, OrbState.Listening, 3, _ => amplitude, t => { if (t > 2) peak = Math.Max(peak, Displacement(motion)); });
        return peak;
    }

    // ---- At rest ------------------------------------------------------------------------------------------------

    [Fact]
    public void AtRestTheBoundaryIsTheReferencesSmile()
    {
        // Measured from the reference, in radii below the orb's center: about 0.265 at the middle, 0.11 half way out
        // either side, and 0.05 to 0.1 at the flanks; the same either side.
        Assert.InRange(Shape.RestBoundary(0), 0.255, 0.275);
        Assert.InRange(Shape.RestBoundary(0.5), 0.10, 0.13);
        Assert.InRange(Shape.RestBoundary(-0.5), 0.10, 0.13);
        Assert.InRange(Shape.RestBoundary(0.75), 0.05, 0.10);
        Assert.Equal(Shape.RestBoundary(0.3), Shape.RestBoundary(-0.3), 6);

        var motion = new OrbMotion(Shape);
        motion.Snap(OrbState.Idle);
        Assert.All(Grid, x => Assert.Equal(Shape.RestBoundary(x), motion.BoundaryAt(x), 9));
        Assert.False(motion.IsReacting);
    }

    [Fact]
    public void IdleOnlyBreathesAndIgnoresSound()
    {
        var motion = new OrbMotion(Shape);
        var largest = 0.0;
        Run(motion, OrbState.Idle, 8, _ => 1.0, _ => largest = Math.Max(largest, Displacement(motion)));
        Assert.InRange(largest, 0.001, Shape.Breath * 1.05);
        Assert.Equal(0, motion.Level);
        Assert.Equal(0, motion.SwellCount);
        Assert.Equal(0, motion.Pose.Tint);
    }

    // ---- Following the sound ------------------------------------------------------------------------------------

    [Fact]
    public void LouderSoundMovesTheBoundaryMore()
    {
        var quiet = PeakDisplacement(0.1);
        var soft = PeakDisplacement(0.3);
        var loud = PeakDisplacement(0.6);
        var loudest = PeakDisplacement(1.0);
        Assert.True(quiet < soft && soft < loud && loud < loudest, $"{quiet:F3} {soft:F3} {loud:F3} {loudest:F3}");
        Assert.True(loudest > 0.25, $"Full loudness moved the boundary only {loudest:F3} radii.");
    }

    [Fact]
    public void QuietSpeechIsSubtlerButStillVisible()
    {
        var quiet = PeakDisplacement(0.08);
        var loudest = PeakDisplacement(1.0);
        Assert.True(quiet > 0.012, $"Quiet speech moved the boundary only {quiet:F4} radii, which cannot be seen.");
        Assert.True(quiet < loudest * 0.4, $"Quiet speech moved it {quiet:F3} against {loudest:F3} for the loudest.");
    }

    [Fact]
    public void RoomNoiseShowsNothing()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Listening, 2, _ => Shape.Gate * 0.9);
        Assert.Equal(0, motion.Level);
        Assert.Equal(0, motion.Envelope);
        Assert.False(motion.IsReacting);
        Assert.InRange(Displacement(motion), 0, Shape.Breath * 1.05);
    }

    [Fact]
    public void WhenTheSoundStopsTheBoundarySettlesBackToTheReferenceShape()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Listening, 3, t => 0.5 + (0.5 * Math.Sin(t * 9)));
        Assert.True(Displacement(motion) > 0.03 || motion.Level > 0.05, "It was not moving to begin with.");

        // Settling is smooth: the loudness it shows only falls, and by a small step each frame.
        var previous = motion.Level;
        var largestStep = 0.0;
        var rose = false;
        Run(motion, OrbState.Listening, 6, _ => 0, _ =>
        {
            rose |= motion.Level > previous + 1e-12;
            largestStep = Math.Max(largestStep, previous - motion.Level);
            previous = motion.Level;
        });

        Assert.False(rose, "The loudness shown rose while the sound was silent.");
        Assert.True(largestStep < 0.06, $"The loudness shown fell {largestStep:F3} in a frame while settling.");
        Assert.Equal(0, motion.Level);
        Assert.Equal(0, motion.Envelope);
        Assert.Equal(0, motion.SwellCount);
        Assert.False(motion.IsReacting);
        Assert.InRange(Displacement(motion), 0, Shape.Breath * 1.05);
    }

    [Fact]
    public void JitteryInputIsSmoothedAndAStepRisesOverSeveralFrames()
    {
        // The input flips between silence and full scale every frame: the loudness the orb shows must not flicker
        // with it, and neither may the boundary's lift.
        var motion = new OrbMotion(Shape);
        var flip = false;
        var previous = 0.0;
        var largestStep = 0.0;
        var (low, high) = (double.MaxValue, double.MinValue);
        Run(motion, OrbState.Listening, 3, _ => (flip = !flip) ? 1.0 : 0.0, t =>
        {
            if (t > 1)
            {
                largestStep = Math.Max(largestStep, Math.Abs(motion.Level - previous));
                (low, high) = (Math.Min(low, motion.Level), Math.Max(high, motion.Level));
            }

            previous = motion.Level;
        });
        Assert.True(largestStep < 0.08, $"Jittery input moved the loudness shown {largestStep:F3} in one frame.");
        Assert.True(high - low < 0.12, $"Jittery input made the loudness shown swing {high - low:F3}.");

        // A sudden loud sound rises quickly but not in one frame.
        var step = new OrbMotion(Shape);
        step.Update(1, OrbState.Listening, TimeSpan.FromSeconds(1.0 / 60));
        Assert.InRange(step.Level, 0.1, 0.5);
        Run(step, OrbState.Listening, 0.3, _ => 1);
        Assert.True(step.Level > 0.9);
    }

    [Fact]
    public void TheBoundaryDeformsAlongItsLengthRatherThanMovingAsARigidBar()
    {
        var motion = new OrbMotion(Shape);
        var widest = 0.0;
        Run(motion, OrbState.Listening, 3, _ => 0.7, t =>
        {
            if (t > 1)
            {
                // The boundary's displacement differs from place to place along it.
                var moves = Grid.Where(x => Math.Abs(x) < 0.7).Select(x => motion.BoundaryAt(x) - Shape.RestBoundary(x)).ToArray();
                widest = Math.Max(widest, moves.Max() - moves.Min());
            }
        });
        Assert.True(widest > 0.05, $"The boundary's displacement varied only {widest:F3} radii along its length.");
    }

    [Fact]
    public void TheMotionNeverRepeatsItself()
    {
        // At a steady loudness, no two moments 2 s or more apart have the same boundary.
        var motion = new OrbMotion(Shape);
        var samples = new List<double[]>();
        var next = 0.0;
        Run(motion, OrbState.Listening, 40, _ => 0.6, t =>
        {
            if (t >= next)
            {
                samples.Add([.. new[] { -0.6, -0.3, 0, 0.3, 0.6 }.Select(motion.BoundaryAt)]);
                next += 0.1;
            }
        });

        var closest = double.MaxValue;
        for (var i = 100; i < samples.Count; i++)
        {
            for (var j = 0; j < i - 20; j++)
            {
                closest = Math.Min(closest, samples[i].Zip(samples[j], (a, b) => Math.Abs(a - b)).Max());
            }
        }

        Assert.True(closest > 0.004, $"Two moments of the boundary were {closest:F5} radii apart at most.");
    }

    [Fact]
    public void ASoundStartingThrowsUpASwellThatTravelsAndDiesAway()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Listening, 0.5, _ => 0);
        Assert.Equal(0, motion.SwellCount);
        motion.Update(0.8, OrbState.Listening, TimeSpan.FromMilliseconds(16));
        Assert.Equal(1, motion.SwellCount);

        // A steady sound throws no more; the swell is gone by the time the sound has settled.
        Run(motion, OrbState.Listening, 5, _ => 0.8);
        Assert.Equal(0, motion.SwellCount);
    }

    [Fact]
    public void SameSeedGivesSameMotionAndDifferentSeedsDiffer()
    {
        double[] Trace(int seed)
        {
            var motion = new OrbMotion(Shape, seed);
            var trace = new List<double>();
            Run(motion, OrbState.Listening, 3, t => 0.5 + (0.5 * Math.Sin(t * 7)), _ => trace.Add(motion.BoundaryAt(0.2)));
            return [.. trace];
        }

        Assert.Equal(Trace(1), Trace(1));
        Assert.NotEqual(Trace(1), Trace(2));
    }

    // ---- The other states ---------------------------------------------------------------------------------------

    [Fact]
    public void ThinkingShimmersSteadilyWhateverTheSound()
    {
        var silent = new OrbMotion(Shape);
        var noisy = new OrbMotion(Shape);
        var moved = 0.0;
        Run(silent, OrbState.Thinking, 6, _ => 0, _ => moved = Math.Max(moved, Displacement(silent)));
        Run(noisy, OrbState.Thinking, 6, t => 0.5 + (0.5 * Math.Sin(t * 11)));

        // The sound makes no difference at all.
        Assert.Equal(0, noisy.Level);
        Assert.Equal(0, noisy.Envelope);
        Assert.All(Grid, x => Assert.Equal(silent.BoundaryAt(x), noisy.BoundaryAt(x), 9));

        // It does move, gently, and a glint travels along it.
        Assert.InRange(moved, 0.012, 0.09);
        Assert.True(silent.Pose.SheenStrength > 0.9);
        Assert.InRange(Math.Abs(silent.Pose.Sheen), 0, 1);
        Assert.True(silent.IsReacting || silent.Thinking > 0.9);
    }

    [Fact]
    public void ErrorSagsAndWarmsSettlesAndIgnoresSound()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Error, 3, _ => 1.0);
        Assert.Equal(0, motion.Level);
        Assert.InRange(motion.Pose.Tint, Shape.ErrorTint * 0.95, Shape.ErrorTint * 1.001);
        Assert.Equal(0, motion.Pose.SheenStrength, 3);

        // The smile flattens and the flanks sag.
        Assert.True(Math.Abs(motion.BoundaryAt(0) - Shape.RestBoundary(0)) > 0.05);
        Assert.True(motion.BoundaryAt(0.8) > Shape.RestBoundary(0.8) + 0.03);

        // The moment of unrest has died away, and it holds still.
        Assert.False(motion.IsReacting);
        var held = motion.BoundaryAt(0.3);
        Run(motion, OrbState.Error, 1, _ => 1.0);
        Assert.InRange(Math.Abs(held - motion.BoundaryAt(0.3)), 0, Shape.Breath * 1.05);
    }

    [Fact]
    public void ErrorArrivesWithABriefJoltAndLeavingRestoresTheOrb()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Idle, 0.5, _ => 0);
        var largest = 0.0;
        var reference = motion.BoundaryAt(0);
        Run(motion, OrbState.Error, 0.4, _ => 0, _ => largest = Math.Max(largest, Math.Abs(motion.BoundaryAt(0) - reference)));
        Assert.True(largest > 0.03, $"The error arrived without any jolt ({largest:F3}).");

        Run(motion, OrbState.Idle, 3, _ => 0);
        Assert.Equal(0, motion.Pose.Tint, 2);
        Assert.InRange(Displacement(motion), 0, Shape.Breath * 1.1);
    }

    [Fact]
    public void LeavingListeningLetsTheSoundFadeAway()
    {
        var motion = new OrbMotion(Shape);
        Run(motion, OrbState.Listening, 2, _ => 1.0);
        Assert.True(motion.Level > 0.9);
        Run(motion, OrbState.Idle, 4, _ => 1.0);
        Assert.Equal(0, motion.Level);
        Assert.Equal(0, motion.Envelope);
        Assert.Equal(0, motion.Listening, 3);
    }

    // ---- The demo and microphone inputs ---------------------------------------------------------------------------

    [Fact]
    public void InventedSpeechIsSpeechShaped()
    {
        var speech = new MockSpeechAmplitude(seed: 7);
        var values = Enumerable.Range(0, 3000).Select(i => speech.At(i * 0.02)).ToArray();
        Assert.All(values, value => Assert.InRange(value, 0, 1));
        Assert.True(values.Count(value => value == 0) > values.Length * 0.15, "There should be pauses of silence.");
        Assert.True(values.Count(value => value > 0.05) > values.Length * 0.25, "There should be speech.");
        Assert.Contains(values, value => value > 0.6);
        Assert.Contains(values, value => value is > 0.03 and < 0.22);

        // It is smooth: never a jump from one 16 ms to the next.
        var largest = Enumerable.Range(0, 3000).Max(i => Math.Abs(speech.At((i + 1) * 0.016) - speech.At(i * 0.016)));
        Assert.True(largest < 0.4, $"Invented speech jumped {largest:F2} in 16 ms.");
    }

    [Fact]
    public void InventedSpeechIsRepeatablePerSeedAndDiffersBetweenSeeds()
    {
        double[] Sample(int seed) => [.. Enumerable.Range(0, 400).Select(i => new MockSpeechAmplitude(seed).At(i * 0.05))];
        Assert.Equal(Sample(3), Sample(3));
        Assert.NotEqual(Sample(3), Sample(4));

        // Read from a clock, it follows that clock.
        var now = TimeSpan.Zero;
        var speech = new MockSpeechAmplitude(() => now, 3);
        var reads = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            now = TimeSpan.FromSeconds(i * 0.05);
            reads.Add(speech.ReadAmplitude());
        }

        Assert.Equal(Sample(3).Length, reads.Count);
        Assert.All(Sample(3).Zip(reads), pair => Assert.Equal(pair.First, pair.Second, 9));
    }

    [Fact]
    public void AMicrophoneLevelBecomesANormalizedAmplitude()
    {
        var level = 0.0;
        var amplitude = new VoiceLevelAmplitude(new FixedLevel(() => level));
        Assert.Equal(0, amplitude.ReadAmplitude());
        level = 0.0018; // -55 dB: the room.
        Assert.InRange(amplitude.ReadAmplitude(), 0, 0.01);
        level = 0.03; // Ordinary speech, about -30 dB.
        Assert.InRange(amplitude.ReadAmplitude(), 0.4, 0.9);
        level = 0.1; // -20 dB: close, raised speech.
        Assert.Equal(1, amplitude.ReadAmplitude(), 6);
        level = 1;
        Assert.Equal(1, amplitude.ReadAmplitude(), 6);
    }

    private sealed class FixedLevel(Func<double> read) : Assistant.UI.Voice.IVoiceLevelSource
    {
        public double ReadLevel() => read();
    }
}
