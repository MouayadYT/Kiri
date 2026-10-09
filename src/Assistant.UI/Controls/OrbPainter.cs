using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Orb;

namespace Assistant.UI.Controls;

/// <summary>
/// Draws the assistant orb on a unit sphere, radius 1 at the origin, for <see cref="AssistantOrb"/>. Every brush is
/// built once, in the unit sphere's own coordinates, so a frame costs only the three geometries that follow the
/// boundary, a small bitmap of the light above it, and a few dozen draws. The values are measured from the reference
/// (a 2x image, the orb 206 px across): its dark upper half, the bright boundary with its soft bloom above and its
/// color fringes, the glass below that is darkest under the flanks and lightest at the bottom, its bright rim, and
/// its soft shadow, mostly below.
/// </summary>
internal sealed class OrbPainter
{
    // The boundary is drawn a little wider than the sphere, so its ends never show.
    private const double Reach = 1.08;

    private static readonly Geometry Sphere = Freeze(new EllipseGeometry(new Point(0, 0), 1, 1));

    // ---- The shadow ------------------------------------------------------------------------------------------------

    // Measured from the reference: a soft black disc offset 0.3 down, half dark just under the orb and gone 1.9 out.
    private static readonly Brush Shadow = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.00, 0x8C, 0), (0.42, 0x80, 0), (0.56, 0x4D, 0), (0.67, 0x2B, 0), (0.79, 0x13, 0), (0.92, 0x05, 0), (1.00, 0x00, 0));

    // ---- The dark upper half ---------------------------------------------------------------------------------------

    private static readonly Brush Body = Radial(new Point(0.5, 0.42), 0.75, 0.75,
        (0.00, 0xFF, 0x17), (0.55, 0xFF, 0x15), (0.85, 0xFF, 0x10), (1.00, 0xFF, 0x0B));

    // The sphere's edge catches light at its sides, more than at its top.
    private static readonly Brush SideRim = Horizontal(-1, 1,
        (0.000, White(0.26)), (0.030, White(0.20)), (0.060, White(0.12)), (0.110, White(0.04)), (0.180, White(0)),
        (0.820, White(0)), (0.890, White(0.04)), (0.940, White(0.12)), (0.970, White(0.20)), (1.000, White(0.26)));

    private static readonly Brush WarmSide = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.0, Color.FromArgb(0x20, 150, 100, 60)), (1.0, Color.FromArgb(0, 150, 100, 60)));

    private static readonly Brush CoolSide = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.0, Color.FromArgb(0x1E, 70, 110, 180)), (1.0, Color.FromArgb(0, 70, 110, 180)));

    // ---- The bright boundary ---------------------------------------------------------------------------------------

    // The band itself: white across the middle of the smile, warm toward its left end and cool toward its right, as in
    // the reference, fading out toward both flanks.
    private static readonly Brush Core = Horizontal(-Reach, Reach,
        (0.000, Color.FromArgb(0x00, 255, 190, 130)), (0.083, Color.FromArgb(0x0A, 255, 200, 150)),
        (0.176, Color.FromArgb(0x28, 255, 215, 175)), (0.268, Color.FromArgb(0x61, 255, 232, 208)),
        (0.315, Color.FromArgb(0xA8, 255, 246, 236)), (0.361, White(1)), (0.639, White(1)),
        (0.685, Color.FromArgb(0xA8, 240, 246, 255)), (0.732, Color.FromArgb(0x61, 225, 238, 255)),
        (0.824, Color.FromArgb(0x28, 200, 225, 255)), (0.917, Color.FromArgb(0x0A, 170, 210, 255)),
        (1.000, Color.FromArgb(0x00, 150, 200, 255)));

    // The fringes where the light breaks up along the boundary's edge, orange above it and blue below.
    private static readonly Pen WarmFringe = new(Horizontal(-Reach, Reach,
        (0.00, Color.FromArgb(0, 255, 150, 70)), (0.10, Color.FromArgb(0x90, 255, 150, 70)),
        (0.28, Color.FromArgb(0xCC, 255, 160, 80)), (0.50, Color.FromArgb(0x30, 255, 170, 100)),
        (0.72, Color.FromArgb(0xB0, 255, 165, 85)), (0.92, Color.FromArgb(0x90, 255, 150, 70)),
        (1.00, Color.FromArgb(0, 255, 150, 70))), 0.014);

    private static readonly Pen CoolFringe = new(Horizontal(-Reach, Reach,
        (0.00, Color.FromArgb(0, 110, 200, 255)), (0.10, Color.FromArgb(0xA0, 110, 200, 255)),
        (0.28, Color.FromArgb(0xD0, 120, 210, 255)), (0.50, Color.FromArgb(0x30, 150, 220, 255)),
        (0.72, Color.FromArgb(0xB8, 120, 200, 255)), (0.92, Color.FromArgb(0xA0, 110, 190, 255)),
        (1.00, Color.FromArgb(0, 110, 190, 255))), 0.014);

    // The glint that passes along the boundary while thinking.
    private static readonly Brush Glint = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.0, White(0.75)), (0.35, White(0.45)), (1.0, White(0)));

    // ---- The glass below --------------------------------------------------------------------------------------------

    // Lightest near the bottom middle, darker toward the sides.
    private static readonly Brush Glass = new RadialGradientBrush
    {
        MappingMode = BrushMappingMode.Absolute, Center = new Point(0, 0.80), GradientOrigin = new Point(0, 0.80),
        RadiusX = 1.05, RadiusY = 0.95,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(208, 208, 206), 0.00), new GradientStop(Color.FromRgb(200, 200, 199), 0.35),
            new GradientStop(Color.FromRgb(176, 176, 175), 0.60), new GradientStop(Color.FromRgb(140, 140, 139), 0.80),
            new GradientStop(Color.FromRgb(100, 100, 100), 1.00),
        },
    };

    // Dark under the boundary's flanks, fading toward the middle and downward: a corner of shade at each side.
    private static readonly Brush FlankShade = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.00, Black(0.55)), (0.30, Black(0.40)), (0.55, Black(0.22)), (0.78, Black(0.08)), (1.00, Black(0)));

    // The glass brightens right under the bright band, where it reflects it.
    private static readonly Brush Lens = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.0, White(0.20)), (0.4, White(0.13)), (0.75, White(0.05)), (1.0, White(0)));

    private static readonly Brush BottomEdge = Radial(new Point(0.5, 0.5), 0.5, 0.5,
        (0.82, Black(0)), (1.0, Black(0.14)));

    // Where the glass meets the sphere's edge it is lit from outside, most at the sides and hardly at all at the bottom:
    // three soft rings, wide and faint to fine and bright, whose strength is greatest toward the sides.
    private static readonly Pen[] EdgeGlow = CreateEdgeGlow();

    // A bright fine rim along the lower edge, brightest at the bottom.
    private static readonly Pen LowerRim = new(Horizontal(-1, 1,
        (0.00, Color.FromArgb(0x90, 255, 255, 255)), (0.12, Color.FromArgb(0xB0, 255, 245, 235)),
        (0.30, Color.FromArgb(0xE0, 255, 255, 255)), (0.50, White(1)), (0.70, Color.FromArgb(0xE0, 255, 255, 255)),
        (0.88, Color.FromArgb(0xB0, 240, 248, 255)), (1.00, Color.FromArgb(0x90, 255, 255, 255))), 0.022);

    private static readonly Geometry LowerRimArc = CreateLowerRim();

    // The whole sphere's fine edge, dim at the top and bright at the bottom.
    private static readonly Pen Rim = new(Vertical(
        (0.00, Color.FromArgb(0x30, 255, 255, 255)), (0.45, Color.FromArgb(0x30, 255, 255, 255)),
        (0.60, Color.FromArgb(0x70, 255, 255, 255)), (1.00, Color.FromArgb(0xE8, 255, 255, 255))), 0.016);

    // ---- The error ---------------------------------------------------------------------------------------------------

    private static readonly Brush ErrorWash = new SolidColorBrush(Color.FromArgb(0x38, 255, 84, 70));

    private static readonly Brush ErrorCore = Horizontal(-Reach, Reach,
        (0.00, Color.FromArgb(0, 255, 90, 70)), (0.15, Color.FromArgb(0xB0, 255, 96, 78)),
        (0.35, Color.FromArgb(0xF0, 255, 110, 90)), (0.50, Color.FromArgb(0xFF, 255, 130, 108)),
        (0.65, Color.FromArgb(0xF0, 255, 110, 90)), (0.85, Color.FromArgb(0xB0, 255, 96, 78)),
        (1.00, Color.FromArgb(0, 255, 90, 70)));

    // ---- The bloom bitmap -------------------------------------------------------------------------------------------

    // The light above the boundary is worked out for each pixel of a small bitmap, laid over the sphere's upper half
    // and smoothed as it is scaled, since no stack of vector shapes can be brighter and taller at the middle of the
    // smile than at its flanks, as the reference is, without showing steps.
    private const int BloomWidth = 128;
    private const int BloomHeight = 92;
    private const double BloomTop = -1.02;
    private const double BloomBottom = 0.80;

    // Lookups of the bloom's fall-off run from a height of 0 to this many radii above the boundary.
    private const double LutReach = 1.0;

    private static readonly float[] BloomLut = CreateBloomLut();

    private readonly WriteableBitmap _bloom = new(BloomWidth, BloomHeight, 96, 96, PixelFormats.Pbgra32, null);
    private readonly byte[] _pixels = new byte[BloomWidth * BloomHeight * 4];

    static OrbPainter()
    {
        foreach (var pen in new[] { WarmFringe, CoolFringe, LowerRim, Rim })
        {
            pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
            pen.Freeze();
        }
    }

    /// <summary>
    /// Draws the orb as <paramref name="motion"/> has it, on the unit sphere, with <paramref name="points"/> samples
    /// along its boundary.
    /// </summary>
    public void Paint(DrawingContext context, OrbMotion motion, bool shadow, int points)
    {
        if (shadow)
        {
            context.DrawEllipse(Shadow, null, new Point(0, 0.30), 1.9, 1.9);
        }

        var pose = motion.Pose;
        var line = new Point[points + 1];
        var band = new Point[points + 1];
        for (var i = 0; i <= points; i++)
        {
            var x = -Reach + (2 * Reach * i / points);
            var y = motion.BoundaryAt(x);
            line[i] = new Point(x, y);
            band[i] = new Point(x, y - motion.ThicknessAt(x, pose));
        }

        var boundary = Polyline(line, closeDown: false);
        var glass = Polyline(line, closeDown: true);
        var core = Band(line, band);

        context.PushClip(Sphere);
        context.DrawRectangle(Body, null, new Rect(-1, -1, 2, 2));
        context.DrawRectangle(SideRim, null, new Rect(-1, -1, 2, 2));
        context.DrawEllipse(WarmSide, null, new Point(-0.80, 0.08), 0.30, 0.55);
        context.DrawEllipse(CoolSide, null, new Point(0.84, 0.05), 0.30, 0.55);

        // The bloom above the boundary, white and, in an error, red; the glass, drawn next, covers the half of it that
        // lies below.
        FillBloom(motion, pose);
        context.DrawImage(_bloom, new Rect(-Reach, BloomTop, 2 * Reach, BloomBottom - BloomTop));
        var glow = Math.Clamp(pose.Glow, 0, 2);

        // The glass below the boundary, dark under its flanks and lit under its middle.
        context.DrawGeometry(Glass, null, glass);
        context.PushClip(glass);
        DrawShade(context, -0.95, motion.BoundaryAt(-0.85));
        DrawShade(context, 0.95, motion.BoundaryAt(0.85));
        context.DrawEllipse(BottomEdge, null, new Point(0, 0), 1, 1);
        foreach (var pen in EdgeGlow)
        {
            context.DrawEllipse(null, pen, new Point(0, 0), 1, 1);
        }

        var lens = Math.Max(0.4, pose.Lens);
        context.PushOpacity(Math.Min(1, lens));
        context.DrawEllipse(Lens, null, new Point(0, motion.BoundaryAt(0) + 0.02), 0.55 * Math.Sqrt(lens), 0.20 * Math.Sqrt(lens));
        context.Pop();
        context.Pop();

        // The bright band itself, then the colors that fringe the boundary's edge.
        context.PushOpacity(Math.Min(1, glow));
        context.DrawGeometry(Core, null, core);
        context.Pop();
        context.PushOpacity(0.75);
        DrawFringe(context, boundary, WarmFringe, -0.007);
        DrawFringe(context, boundary, CoolFringe, 0.007);
        context.Pop();

        if (pose.SheenStrength > 0.001 && !double.IsNaN(pose.Sheen))
        {
            context.PushOpacity(pose.SheenStrength);
            context.DrawEllipse(Glint, null, new Point(pose.Sheen, motion.BoundaryAt(pose.Sheen) - 0.02), 0.30, 0.09);
            context.Pop();
        }

        if (pose.Tint > 0.001)
        {
            // The error's light: a restrained red band over the white one, and a faint warm wash on the glass.
            context.PushOpacity(pose.Tint);
            context.DrawGeometry(ErrorWash, null, glass);
            context.PushOpacity(0.85);
            context.DrawGeometry(ErrorCore, null, core);
            context.Pop();
            context.Pop();
        }

        context.DrawGeometry(null, LowerRim, LowerRimArc);
        context.Pop();
        context.DrawEllipse(null, Rim, new Point(0, 0), 0.992, 0.992);
    }

    // A corner of shade in the glass at one side, whose brightest point is where the boundary meets the rim there.
    private static void DrawShade(DrawingContext context, double x, double y) =>
        context.DrawEllipse(FlankShade, null, new Point(x, y), 1.05, 1.05);

    private static void DrawFringe(DrawingContext context, Geometry boundary, Pen pen, double offset)
    {
        context.PushTransform(new TranslateTransform(0, offset));
        context.DrawGeometry(null, pen, boundary);
        context.Pop();
    }

    // Works out the bloom for every pixel: strongest and tallest at the middle of the smile, weaker and lower toward the
    // flanks, warm on the left and cool on the right, and reddened by an error.
    private void FillBloom(OrbMotion motion, OrbPose pose)
    {
        var glow = Math.Min(1, Math.Clamp(pose.Glow, 0, 1.5));
        var reach = 1 + (0.5 * (pose.Thickness - 1));
        var error = Math.Clamp(pose.Tint, 0, 1);
        for (var column = 0; column < BloomWidth; column++)
        {
            var x = -Reach + (2 * Reach * (column + 0.5) / BloomWidth);
            var boundary = motion.BoundaryAt(x);
            var strength = glow * (0.1 + (0.9 * Math.Exp(-Math.Pow(x / 0.50, 2))));
            var scale = reach * (0.20 + (0.80 * Math.Exp(-Math.Pow(x / 0.50, 2))));

            // Warm toward the left, cool toward the right, redder in an error.
            var side = Math.Clamp((Math.Abs(x) - 0.15) / 0.65, 0, 1);
            var (red, green, blue) = x < 0
                ? (255.0, 255 - (33 * side), 255 - (65 * side))
                : (255 - (50 * side), 255 - (27 * side), 255.0);
            (red, green, blue) = (red + ((255 - red) * error), green + ((110 - green) * error), blue + ((92 - blue) * error));
            for (var row = 0; row < BloomHeight; row++)
            {
                var y = BloomTop + ((BloomBottom - BloomTop) * (row + 0.5) / BloomHeight);
                var height = (boundary - y) / scale;
                var alpha = 0.0;
                if (height <= 0)
                {
                    alpha = BloomLut[0];
                }
                else if (height < LutReach)
                {
                    var position = height / LutReach * (BloomLut.Length - 1);
                    var index = (int)position;
                    alpha = BloomLut[index] + ((BloomLut[Math.Min(index + 1, BloomLut.Length - 1)] - BloomLut[index]) * (position - index));
                }

                alpha = Math.Min(1, alpha * strength);
                var offset = ((row * BloomWidth) + column) * 4;
                _pixels[offset] = (byte)Math.Round(blue * alpha);
                _pixels[offset + 1] = (byte)Math.Round(green * alpha);
                _pixels[offset + 2] = (byte)Math.Round(red * alpha);
                _pixels[offset + 3] = (byte)Math.Round(255 * alpha);
            }
        }

        _bloom.WritePixels(new Int32Rect(0, 0, BloomWidth, BloomHeight), _pixels, BloomWidth * 4, 0);
    }

    // How the light above the boundary falls off, read from the reference's middle column: its brightness at heights
    // above the boundary (in radii), as a share of the band's, by linear steps between the measurements.
    private static float[] CreateBloomLut()
    {
        (double Height, double Strength)[] target =
        [
            (0.00, 1.00), (0.13, 0.96), (0.143, 0.94), (0.18, 0.72), (0.22, 0.50), (0.26, 0.38), (0.30, 0.29), (0.375, 0.20),
            (0.45, 0.14), (0.53, 0.094), (0.61, 0.064), (0.69, 0.04), (0.76, 0.02), (0.84, 0.01), (0.95, 0.0), (1.0, 0.0),
        ];
        var lut = new float[512];
        for (var i = 0; i < lut.Length; i++)
        {
            var height = LutReach * i / (lut.Length - 1);
            var j = 1;
            while (j < target.Length - 1 && height > target[j].Height)
            {
                j++;
            }

            var (h0, s0) = target[j - 1];
            var (h1, s1) = target[j];
            lut[i] = (float)Math.Clamp(s0 + ((s1 - s0) * (height - h0) / (h1 - h0)), 0, 1);
        }

        return lut;
    }

    // The boundary as an open line, or, closed down, as the region below it (the glass).
    private static Geometry Polyline(Point[] points, bool closeDown)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: closeDown, isClosed: closeDown);
            context.PolyLineTo(points.AsSpan(1).ToArray(), isStroked: true, isSmoothJoin: true);
            if (closeDown)
            {
                context.LineTo(new Point(points[^1].X, 1.3), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(points[0].X, 1.3), isStroked: false, isSmoothJoin: false);
            }
        }

        return Freeze(geometry);
    }

    // The region between two lines, the lower one first.
    private static Geometry Band(Point[] lower, Point[] upper)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(lower[0], isFilled: true, isClosed: true);
            context.PolyLineTo(lower.AsSpan(1).ToArray(), isStroked: false, isSmoothJoin: true);
            context.PolyLineTo(Enumerable.Reverse(upper).ToArray(), isStroked: false, isSmoothJoin: true);
        }

        return Freeze(geometry);
    }

    // The lower edge of the sphere, from just below its left side, round the bottom, to just below its right.
    private static Geometry CreateLowerRim()
    {
        const double radius = 0.992;
        var angle = 7 * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(-radius * Math.Cos(angle), radius * Math.Sin(angle)), isFilled: false, isClosed: false);
            context.ArcTo(new Point(radius * Math.Cos(angle), radius * Math.Sin(angle)), new Size(radius, radius), 0,
                isLargeArc: true, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);
        }

        return Freeze(geometry);
    }

    private static Pen[] CreateEdgeGlow()
    {
        var sides = Horizontal(-1, 1,
            (0.00, White(1.0)), (0.10, White(0.75)), (0.20, White(0.35)), (0.30, White(0.08)), (0.5, White(0)),
            (0.70, White(0.08)), (0.80, White(0.35)), (0.90, White(0.75)), (1.00, White(1.0)));

        // Rings centered on the edge, half of each clipped away: each one covers the depth from the edge inward that is
        // its half-width, so they pile up toward the edge. Their strengths are worked out so that the light falls off
        // inward from the edge as the reference's does, from about half as bright as white to nothing in 0.16 radii.
        double[] halfWidths = [0.16, 0.13, 0.105, 0.085, 0.065, 0.048, 0.033, 0.02, 0.01];
        (double Depth, double Strength)[] target = [(0.0, 0.55), (0.015, 0.42), (0.03, 0.30), (0.06, 0.18), (0.10, 0.09), (0.13, 0.03), (0.17, 0.0)];
        var pens = new Pen[halfWidths.Length];
        var laid = 0.0;
        for (var i = 0; i < halfWidths.Length; i++)
        {
            var inner = i + 1 < halfWidths.Length ? halfWidths[i + 1] : 0;
            var wanted = StrengthAt(target, (halfWidths[i] + inner) / 2);
            var alpha = wanted > laid ? 1 - ((1 - wanted) / (1 - laid)) : 0;
            laid = Math.Max(laid, wanted);
            pens[i] = new Pen(Scaled(sides, alpha), halfWidths[i] * 2);
            pens[i].Freeze();
        }

        return pens;
    }

    // Linear steps between measurements, by depth or height; nothing past the last.
    private static double StrengthAt((double Height, double Strength)[] target, double height)
    {
        for (var i = 1; i < target.Length; i++)
        {
            if (height <= target[i].Height)
            {
                var (h0, s0) = target[i - 1];
                var (h1, s1) = target[i];
                return s0 + ((s1 - s0) * (height - h0) / (h1 - h0));
            }
        }

        return 0;
    }

    // The same gradient, with its strength scaled.
    private static Brush Scaled(Brush brush, double factor)
    {
        var scaled = brush.Clone();
        scaled.Opacity = factor;
        scaled.Freeze();
        return scaled;
    }

    // ---- Brush helpers ---------------------------------------------------------------------------------------------

    private static Color White(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255);

    private static Color Black(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0);

    // A left-to-right gradient over x from <from> to <to> in the unit sphere's coordinates; offsets run 0 to 1.
    private static Brush Horizontal(double from, double to, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(from, 0), EndPoint = new Point(to, 0),
        };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    // A top-to-bottom gradient over the geometry's own bounds.
    private static Brush Vertical(params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    // A radial gradient over the geometry's bounds.
    private static Brush Radial(Point center, double radiusX, double radiusY, params (double Offset, Color Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY,
        };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    // Stops given as (offset, alpha byte, gray byte).
    private static Brush Radial(Point center, double radiusX, double radiusY, params (double Offset, int Alpha, int Gray)[] stops) =>
        Radial(center, radiusX, radiusY, [.. stops.Select(stop => (stop.Offset, Color.FromArgb((byte)stop.Alpha, (byte)stop.Gray, (byte)stop.Gray, (byte)stop.Gray)))]);

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
