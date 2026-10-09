using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// An image set into the lower part of a card the way the reference's liquid glass holds it, such as a History card's
/// thumbnail. It hangs at the card's bottom across its whole width: the image shows in a column with strongly rounded
/// top corners, <see cref="Inset"/> from the card's sides, and below that the card's glass beside the column ends in
/// rounded tips a little above the card's bottom, so the image flows around them out to the card's own outline and its
/// rounded bottom. The image fills the element, centered and cropped, in its own colors.
/// </summary>
public sealed class CardThumbnail : FrameworkElement
{
    /// <summary>Identifies the <see cref="Source"/> property.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(CardThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="CornerSize"/> property.</summary>
    public static readonly DependencyProperty CornerSizeProperty = RegisterLength(nameof(CornerSize));

    /// <summary>Identifies the <see cref="Inset"/> property.</summary>
    public static readonly DependencyProperty InsetProperty = RegisterLength(nameof(Inset));

    /// <summary>Identifies the <see cref="TopCornerSize"/> property.</summary>
    public static readonly DependencyProperty TopCornerSizeProperty = RegisterLength(nameof(TopCornerSize));

    /// <summary>Identifies the <see cref="TipWidth"/> property.</summary>
    public static readonly DependencyProperty TipWidthProperty = RegisterLength(nameof(TipWidth));

    /// <summary>Identifies the <see cref="TipHeight"/> property.</summary>
    public static readonly DependencyProperty TipHeightProperty = RegisterLength(nameof(TipHeight));

    /// <summary>Identifies the <see cref="TipLift"/> property.</summary>
    public static readonly DependencyProperty TipLiftProperty = RegisterLength(nameof(TipLift));

    // Where the control points of a tip's outer curve lie, as fractions of the curve's chord: along the tip's bottom,
    // and back along the card's outline, which the curve leaves tangentially as high up as the tip's inner side starts
    // to curve. Measured from the reference, where a sliver of the image, up to about 2.5 wide, shows between the tip
    // and the outline.
    private const double TipBottomHandle = 0.3;
    private const double TipOutlineHandle = 0.3;

    private Geometry? _clip;

    public CardThumbnail() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

    /// <summary>The image.</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>How far the card's own corners reach along its edges, which the image's bottom follows.</summary>
    public double CornerSize
    {
        get => (double)GetValue(CornerSizeProperty);
        set => SetValue(CornerSizeProperty, value);
    }

    /// <summary>How far the image's column lies from the card's sides.</summary>
    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    /// <summary>How far the column's top corners reach along its edges.</summary>
    public double TopCornerSize
    {
        get => (double)GetValue(TopCornerSizeProperty);
        set => SetValue(TopCornerSizeProperty, value);
    }

    /// <summary>How far past the column's side the lowest point of the glass beside it lies.</summary>
    public double TipWidth
    {
        get => (double)GetValue(TipWidthProperty);
        set => SetValue(TipWidthProperty, value);
    }

    /// <summary>How far above the lowest point of the glass beside the column its side begins to curve outward.</summary>
    public double TipHeight
    {
        get => (double)GetValue(TipHeightProperty);
        set => SetValue(TipHeightProperty, value);
    }

    /// <summary>How far above the card's bottom the glass beside the column ends.</summary>
    public double TipLift
    {
        get => (double)GetValue(TipLiftProperty);
        set => SetValue(TipLiftProperty, value);
    }

    /// <summary>
    /// Creates the outline of the image in an element of <paramref name="size"/> hanging at a card's bottom across its
    /// width, in the element's coordinates. See the properties of the same names.
    /// </summary>
    public static Geometry CreateOutline(
        Size size, double cornerSize, double inset, double topCornerSize, double tipWidth, double tipHeight,
        double tipLift)
    {
        var (width, height) = (size.Width, size.Height);
        var corner = Math.Min(cornerSize, width / 2);

        // The card reaches above the element, so only its bottom corners fall inside it.
        var cardHeight = height + (2 * corner) + 1;
        var card = PanelShape.CreateGeometry(new Rect(0, height - cardHeight, width, cardHeight), corner);

        var (left, right) = (inset, width - inset);
        var tipBottom = height - tipLift;
        var tipStart = tipBottom - tipHeight;
        var top = Math.Min(topCornerSize, Math.Min((right - left) / 2, tipStart));
        if (right <= left || top < 0 || left - tipWidth <= 0)
        {
            // Too small for glass beside the image: the image fills the card's outline.
            return card;
        }

        // Where the tip's outer curve meets the card's outline on the left, and the outline's direction there, down
        // toward the card's bottom.
        var (meet, along) = OutlineAt(tipStart, height, corner);
        var k = PanelShape.ControlPointRatio;

        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            // Down from the column's top-left corner, across its top and down its right side.
            context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
            context.BezierTo(new Point(left, top * (1 - k)), new Point(left + (top * (1 - k)), 0), new Point(left + top, 0), true, true);
            context.LineTo(new Point(right - top, 0), true, true);
            context.BezierTo(new Point(right - (top * (1 - k)), 0), new Point(right, top * (1 - k)), new Point(right, top), true, true);
            context.LineTo(new Point(right, tipStart), true, true);

            // Around the right tip and out past the card's right side, across below the card, and back around the left
            // tip. Whatever lies outside the card is cut off by its outline.
            var tipRight = new Point(right + tipWidth, tipBottom);
            var meetRight = new Point(width - meet.X, meet.Y);
            var alongRight = new Vector(-along.X, along.Y);
            var chord = (meetRight - tipRight).Length;
            context.BezierTo(new Point(right, tipStart + (k * tipHeight)), new Point(right + ((1 - k) * tipWidth), tipBottom), tipRight, true, true);
            context.BezierTo(tipRight + new Vector(TipBottomHandle * chord, 0), meetRight + (alongRight * TipOutlineHandle * chord), meetRight, true, true);
            context.LineTo(new Point(width + 1, meet.Y), true, true);
            context.LineTo(new Point(width + 1, height + 1), true, true);
            context.LineTo(new Point(-1, height + 1), true, true);
            context.LineTo(new Point(-1, meet.Y), true, true);
            context.LineTo(meet, true, true);

            var tipLeft = new Point(left - tipWidth, tipBottom);
            context.BezierTo(meet + (along * TipOutlineHandle * chord), tipLeft - new Vector(TipBottomHandle * chord, 0), tipLeft, true, true);
            context.BezierTo(new Point(left - ((1 - k) * tipWidth), tipBottom), new Point(left, tipStart + (k * tipHeight)), new Point(left, tipStart), true, true);
        }

        outline.Freeze();
        var image = new CombinedGeometry(GeometryCombineMode.Intersect, card, outline);
        image.Freeze();
        return image;
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        if (Source is not { } source || size.Width <= 0 || size.Height <= 0 || source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        _clip ??= CreateOutline(size, CornerSize, Inset, TopCornerSize, TipWidth, TipHeight, TipLift);

        // Fills the element, centered, cropping whichever sides are too long.
        var scale = Math.Max(size.Width / source.Width, size.Height / source.Height);
        var (drawnWidth, drawnHeight) = (source.Width * scale, source.Height * scale);
        drawingContext.PushClip(_clip);
        drawingContext.DrawImage(source, new Rect((size.Width - drawnWidth) / 2, (size.Height - drawnHeight) / 2, drawnWidth, drawnHeight));
        drawingContext.Pop();
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        _clip = null;
        base.OnRenderSizeChanged(sizeInfo);
    }

    // The point on the left side of the card's outline at y, and the outline's direction there, heading down. The card's
    // bottom-left corner is the curve PanelShape draws, from (0, height - corner) to (corner, height).
    private static (Point Point, Vector Along) OutlineAt(double y, double height, double corner)
    {
        if (y <= height - corner || corner <= 0)
        {
            return (new Point(0, y), new Vector(0, 1));
        }

        var k = PanelShape.ControlPointRatio;
        var p0 = new Point(0, height - corner);
        var p1 = new Point(0, height - corner + (k * corner));
        var p2 = new Point(corner - (k * corner), height);
        var p3 = new Point(corner, height);

        // The curve only ever moves down, so its height finds its parameter by halving.
        var (low, high) = (0.0, 1.0);
        for (var i = 0; i < 40; i++)
        {
            var middle = (low + high) / 2;
            if (Bezier(p0, p1, p2, p3, middle).Y < y) low = middle;
            else high = middle;
        }

        var t = (low + high) / 2;
        var along = (3 * (1 - t) * (1 - t) * (p1 - p0)) + (6 * (1 - t) * t * (p2 - p1)) + (3 * t * t * (p3 - p2));
        along.Normalize();
        return (Bezier(p0, p1, p2, p3, t), along);
    }

    private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        return new Point(
            (u * u * u * p0.X) + (3 * u * u * t * p1.X) + (3 * u * t * t * p2.X) + (t * t * t * p3.X),
            (u * u * u * p0.Y) + (3 * u * u * t * p1.Y) + (3 * u * t * t * p2.Y) + (t * t * t * p3.Y));
    }

    private static DependencyProperty RegisterLength(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(CardThumbnail),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged),
        value => value is double length && length >= 0 && double.IsFinite(length));

    private static void OnShapeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) =>
        ((CardThumbnail)target)._clip = null;
}
