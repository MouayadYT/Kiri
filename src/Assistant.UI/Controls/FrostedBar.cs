using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Assistant.UI.Controls;

/// <summary>
/// A bar of frosted glass along the edge of a window, over a picture of what is behind it, for the image search results' "Report a
/// Concern" strip (PROJECT_SPEC §4.6): the <see cref="Source"/>, a visual in the same window that the bar hangs in front of, drawn blurred
/// behind it under the glass's tint, so that what scrolls under the bar is seen soft and the bar's own content stays readable.
/// </summary>
/// <remarks>It follows its source: it looks at the part of it that lies behind the bar, wherever the bar is laid out, and does nothing without one.
/// Its theme is Themes/Controls/ImageSearch.xaml.</remarks>
[TemplatePart(Name = BlurPart, Type = typeof(Rectangle))]
public sealed class FrostedBar : ContentControl
{
    private const string BlurPart = "PART_Blur";

    // How far the blurred layer reaches past the bar on every side (the template's margin), so that the blur has real picture to take in
    // at the bar's edge rather than fading to nothing there.
    private const double BlurMargin = 24;

    /// <summary>Identifies the <see cref="Source"/> property.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(Visual), typeof(FrostedBar), new PropertyMetadata(null, OnSourceChanged));

    private Rectangle? _blur;
    private VisualBrush? _brush;

    static FrostedBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostedBar), new FrameworkPropertyMetadata(typeof(FrostedBar)));
    }

    /// <summary>Creates the bar.</summary>
    public FrostedBar()
    {
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            Refresh();
        };
        Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
    }

    /// <summary>The visual the bar looks at the part of that lies behind it, blurred.</summary>
    public Visual? Source
    {
        get => (Visual?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _blur = GetTemplateChild(BlurPart) as Rectangle;
        _brush = null;
        Refresh();
    }

    // Brings the picture behind the bar up to date: the part of the source that lies under the bar, and the blur's margin around it.
    private void Refresh()
    {
        if (_blur is null || Source is not { } source || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
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

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

    private static void OnSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is FrostedBar bar)
        {
            bar._brush = null;
            bar.Refresh();
        }
    }
}
