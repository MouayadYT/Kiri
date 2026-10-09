using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Assistant.UI.Controls;

/// <summary>
/// A pill of frosted glass over a picture of what is behind it, for the Visual Intelligence overlay's chips (PROJECT_SPEC §4.6): the
/// <see cref="BackdropSource"/>, a visual that the pill hangs in front of in the same window, drawn blurred behind it, with the
/// glass's light tint, the light along its top and bottom edges and its rim, as the reference has them. The overlay's surface is
/// the source, so the blurred layer is the snapshot as the user sees it, dimming included.
/// </summary>
/// <remarks>
/// The pill follows its source: it looks at the part of it that lies behind the pill, wherever the pill is laid out. It needs a
/// source in the same visual tree, and does nothing without one. Its theme is Themes/Controls/Capture.xaml.
/// </remarks>
[TemplatePart(Name = BackdropPart, Type = typeof(Panel))]
[TemplatePart(Name = BlurPart, Type = typeof(Rectangle))]
public sealed class FrostedPill : ContentControl
{
    private const string BackdropPart = "PART_Backdrop";
    private const string BlurPart = "PART_Blur";

    // How far the blurred layer reaches past the pill on every side, so that the blur has real picture to take in at the pill's edge
    // rather than fading to nothing there. It is the margin the template gives the layer.
    private const double BlurMargin = 10;

    /// <summary>
    /// Identifies the BackdropSource attached property. It is inherited, so one set on the element that holds the chips is the source of
    /// every pill inside it, the ones in a button's template included.
    /// </summary>
    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.RegisterAttached(
        "BackdropSource", typeof(Visual), typeof(FrostedPill),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnBackdropSourceChanged));

    private Panel? _backdrop;
    private Rectangle? _blur;
    private VisualBrush? _brush;

    static FrostedPill()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostedPill), new FrameworkPropertyMetadata(typeof(FrostedPill)));
    }

    /// <summary>Creates the pill.</summary>
    public FrostedPill()
    {
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            Refresh();
        };
        Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
    }

    /// <summary>The visual the pill looks at the part of that lies behind it, blurred.</summary>
    public Visual? BackdropSource
    {
        get => GetBackdropSource(this);
        set => SetBackdropSource(this, value);
    }

    /// <summary>Gets the visual that the pills inside <paramref name="element"/> look at.</summary>
    public static Visual? GetBackdropSource(DependencyObject element) => (Visual?)element.GetValue(BackdropSourceProperty);

    /// <summary>Sets the visual that the pills inside <paramref name="element"/> look at, such as the overlay's surface.</summary>
    public static void SetBackdropSource(DependencyObject element, Visual? value) => element.SetValue(BackdropSourceProperty, value);

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_backdrop is not null)
        {
            _backdrop.SizeChanged -= OnBackdropSizeChanged;
        }

        _backdrop = GetTemplateChild(BackdropPart) as Panel;
        _blur = GetTemplateChild(BlurPart) as Rectangle;
        _brush = null;
        if (_backdrop is not null)
        {
            _backdrop.SizeChanged += OnBackdropSizeChanged;
            ClipBackdrop();
        }

        Refresh();
    }

    // Brings the picture behind the pill up to date: the part of the source that lies under the pill, and the blur's margin around it.
    private void Refresh()
    {
        if (_blur is null || BackdropSource is not { } source || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Point origin;
        try
        {
            origin = TransformToVisual(source).Transform(default);
        }
        catch (InvalidOperationException)
        {
            // Not in the source's tree (yet): there is nothing to look at.
            return;
        }

        var region = new Rect(origin.X - BlurMargin, origin.Y - BlurMargin, ActualWidth + 2 * BlurMargin, ActualHeight + 2 * BlurMargin);
        if (_brush is null || !ReferenceEquals(_brush.Visual, source))
        {
            _brush = new VisualBrush(source)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            _blur.Fill = _brush;
        }

        if (_brush.Viewbox != region)
        {
            _brush.Viewbox = region;
        }
    }

    private void ClipBackdrop()
    {
        if (_backdrop is { ActualWidth: > 0, ActualHeight: > 0 } backdrop)
        {
            backdrop.Clip = PillShape.CreateGeometry(new Rect(backdrop.RenderSize));
        }
    }

    private void OnBackdropSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClipBackdrop();
        Refresh();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

    private static void OnBackdropSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is FrostedPill pill)
        {
            pill._brush = null;
            pill.Refresh();
        }
    }
}
