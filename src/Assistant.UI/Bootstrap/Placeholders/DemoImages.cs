using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Messages;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Synthetic pictures for the sample galleries of <see cref="DemoAnswerProvider"/>: simple drawn scenes and window
/// mock-ups, never the user's files. Landscape and portrait ones show how tiles crop.
/// </summary>
internal static class DemoImages
{
    /// <summary>Four drawn "photos": a sunset, a lake, a forest and a city at night.</summary>
    public static IReadOnlyList<ImageItem> Photos() =>
    [
        new("Sample photo of a sunset", Draw(480, 360, Sunset)),
        new("Sample photo of a mountain lake", Draw(360, 480, Lake)),
        new("Sample photo of a forest", Draw(480, 360, Forest)),
        new("Sample photo of a city at night", Draw(480, 360, City)),
    ];

    /// <summary>A drawn "photo" of a mountain valley under clouds, as if attached to a question about where it is.</summary>
    public static ImageItem Valley() => new("Sample photo of a mountain valley", Draw(480, 360, ValleyScene));

    /// <summary><paramref name="count"/> drawn "screenshots" of app windows, each in its own colors.</summary>
    public static IReadOnlyList<ImageItem> Screenshots(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new ImageItem($"Sample screenshot {index + 1}", Draw(480, 300, context => Window(context, index))))
            .ToArray();

    /// <summary>
    /// A drawn "portrait" 96 across, for a sample contact's photo: a head and shoulders against a background, in colors
    /// that depend on <paramref name="index"/>.
    /// </summary>
    public static BitmapSource Portrait(int index)
    {
        string[][] palettes =
        [
            ["#FFF2D7B0", "#FFC9A06A", "#FF8A5A2B", "#FFF1C9A5"],
            ["#FF3A3A3A", "#FF1B1B1F", "#FF2F5D62", "#FFD9A97F"],
            ["#FFBFD4EC", "#FF7E9FCB", "#FF34507A", "#FFEBC3A1"],
            ["#FFD8E6C6", "#FF8FB37A", "#FF4B6B3B", "#FFE3B48C"],
        ];
        var palette = palettes[Math.Abs(index) % palettes.Length];
        return Draw(96, 96, context =>
        {
            context.DrawRectangle(Vertical(palette[0], palette[1]), null, new Rect(0, 0, 96, 96));
            context.DrawEllipse(Solid(palette[2]), null, new Point(48, 100), 38, 30);
            context.DrawEllipse(Solid(palette[3]), null, new Point(48, 42), 17, 20);
            context.DrawEllipse(Solid(palette[2]), null, new Point(48, 30), 18, 11);
        });
    }

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            draw(context);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Sunset(DrawingContext context)
    {
        context.DrawRectangle(Vertical("#FF3B2C6B", "#FFE0735A", "#FFFFB36B"), null, new Rect(0, 0, 480, 360));
        context.DrawEllipse(Solid("#FFFFE3A1"), null, new Point(300, 250), 46, 46);
        context.DrawGeometry(Solid("#FF2A1B38"), null, Geometry.Parse("M0,260 C90,210 170,240 250,262 S400,230 480,250 V360 H0 Z"));
        context.DrawGeometry(Solid("#FF1A1024"), null, Geometry.Parse("M0,300 C120,270 260,300 480,285 V360 H0 Z"));
    }

    private static void Lake(DrawingContext context)
    {
        context.DrawRectangle(Vertical("#FF7FB6F0", "#FFD9ECFF"), null, new Rect(0, 0, 360, 300));
        context.DrawGeometry(Solid("#FF56657D"), null, Geometry.Parse("M-20,300 L90,150 L150,220 L230,110 L380,300 Z"));
        context.DrawGeometry(Solid("#FFF4F7FB"), null, Geometry.Parse("M230,110 L262,152 L246,148 L232,160 L214,146 L202,150 Z M90,150 L110,177 L96,172 L80,180 L74,172 Z"));
        context.DrawRectangle(Vertical("#FF4E86BE", "#FF1F4E7C"), null, new Rect(0, 300, 360, 180));
        context.DrawGeometry(Solid("#4056657D"), null, Geometry.Parse("M-20,300 L90,410 L150,360 L230,440 L380,300 Z"));
    }

    private static void ValleyScene(DrawingContext context)
    {
        context.DrawRectangle(Vertical("#FF6F9CC4", "#FFB9D3E6"), null, new Rect(0, 0, 480, 360));
        foreach (var (x, y, rx, ry) in new[] { (90.0, 70.0, 110.0, 34.0), (250, 50, 130, 40), (400, 90, 120, 36), (180, 110, 150, 30) })
        {
            context.DrawEllipse(Solid("#E6F4F1EA"), null, new Point(x, y), rx, ry);
        }

        context.DrawGeometry(Solid("#FF8C97A3"), null, Geometry.Parse("M60,250 L170,120 L230,160 L300,95 L420,230 Z"));
        context.DrawGeometry(Solid("#FFF3F4F2"), null, Geometry.Parse("M300,95 L336,138 L316,134 L300,150 L284,136 L262,140 Z M170,120 L196,152 L180,148 L166,160 L150,146 Z"));
        context.DrawGeometry(Solid("#FF3F5B3A"), null, Geometry.Parse("M-10,140 L60,170 L140,300 L210,360 H-10 Z"));
        context.DrawGeometry(Solid("#FF34512F"), null, Geometry.Parse("M490,120 L410,190 L330,300 L280,360 H490 Z"));
        context.DrawGeometry(Vertical("#FF7E9A4B", "#FF56702E"), null, Geometry.Parse("M110,290 C180,260 300,262 370,290 L400,360 H80 Z"));
    }

    private static void Forest(DrawingContext context)
    {
        context.DrawRectangle(Vertical("#FFD4ECD0", "#FF9CCB9A"), null, new Rect(0, 0, 480, 360));
        var random = new Random(7);
        foreach (var (row, color) in new[] { (200.0, "#FF5E9C63"), (260.0, "#FF3E7D48"), (330.0, "#FF245A31") })
        {
            for (var x = -30.0; x < 500; x += 34 + random.Next(20))
            {
                var height = 90 + random.Next(60);
                context.DrawGeometry(Solid(color), null, Geometry.Parse(
                    FormattableString.Invariant($"M{x},{row + 40} L{x + 28},{row - height} L{x + 56},{row + 40} Z")));
            }
        }
    }

    private static void City(DrawingContext context)
    {
        context.DrawRectangle(Vertical("#FF0A1330", "#FF2B3A78"), null, new Rect(0, 0, 480, 360));
        context.DrawEllipse(Solid("#FFF1F1E6"), null, new Point(390, 70), 20, 20);
        var random = new Random(11);
        var windows = Solid("#FFFFD66B");
        for (var x = 0.0; x < 480;)
        {
            var width = 42 + random.Next(40);
            var top = 120 + random.Next(150);
            context.DrawRectangle(Solid(random.Next(2) == 0 ? "#FF111827" : "#FF1C2440"), null, new Rect(x, top, width, 360 - top));
            for (var y = top + 10.0; y < 350; y += 16)
            {
                for (var column = x + 7.0; column < x + width - 10; column += 12)
                {
                    if (random.Next(3) == 0) context.DrawRectangle(windows, null, new Rect(column, y, 5, 7));
                }
            }

            x += width + 4;
        }
    }

    // An app window on a desktop, with a title bar, a sidebar and lines of content; index picks its colors.
    private static void Window(DrawingContext context, int index)
    {
        string[] accents = ["#FF3B82F6", "#FF10B981", "#FFF59E0B", "#FFEC4899", "#FF8B5CF6"];
        var accent = Solid(accents[index % accents.Length]);
        var dark = index % 2 == 1;
        context.DrawRectangle(Vertical(dark ? "#FF1E293B" : "#FF93C5FD", dark ? "#FF0F172A" : "#FFDBEAFE"), null, new Rect(0, 0, 480, 300));

        var window = new Rect(34, 26, 412, 250);
        context.DrawRoundedRectangle(Solid(dark ? "#FF1F2430" : "#FFFFFFFF"), null, window, 10, 10);
        context.DrawRectangle(Solid(dark ? "#FF2A3040" : "#FFF1F3F6"), null, new Rect(window.X, window.Y + 8, window.Width, 22));
        context.DrawRoundedRectangle(Solid(dark ? "#FF2A3040" : "#FFF1F3F6"), null, new Rect(window.X, window.Y, window.Width, 30), 10, 10);
        foreach (var (x, color) in new[] { (50.0, "#FFFF5F57"), (64.0, "#FFFEBC2E"), (78.0, "#FF28C840") })
        {
            context.DrawEllipse(Solid(color), null, new Point(x, window.Y + 15), 4.5, 4.5);
        }

        context.DrawRectangle(Solid(dark ? "#FF262C3A" : "#FFF7F8FA"), null, new Rect(window.X, window.Y + 30, 96, window.Height - 40));
        var line = Solid(dark ? "#FF3A4254" : "#FFE2E6EC");
        for (var i = 0; i < 6; i++)
        {
            context.DrawRoundedRectangle(i == index % 6 ? accent : line, null, new Rect(window.X + 12, window.Y + 46 + i * 22, 72, 9), 4.5, 4.5);
        }

        context.DrawRoundedRectangle(accent, null, new Rect(window.X + 112, window.Y + 46, 150, 16), 5, 5);
        for (var i = 0; i < 6; i++)
        {
            var width = 270 - (i * 37 + index * 23) % 110;
            context.DrawRoundedRectangle(line, null, new Rect(window.X + 112, window.Y + 78 + i * 22, width, 9), 4.5, 4.5);
        }

        context.DrawRoundedRectangle(accent, null, new Rect(window.Right - 96, window.Bottom - 38, 80, 24), 12, 12);
    }

    private static SolidColorBrush Solid(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Vertical(params string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (var i = 0; i < colors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), i / (colors.Length - 1.0)));
        }

        brush.Freeze();
        return brush;
    }
}
