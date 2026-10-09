using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A tile as tall as its width and its <see cref="AspectRatio"/> make it, over a <see cref="Background"/> that shows where the picture
/// in it is missing or still coming: a result card's thumbnail, whose shape is the picture's. Its content fills it, and is cut off by
/// its corners when <see cref="CornerClip"/> is set.
/// </summary>
public sealed class AspectRatioTile : Decorator
{
    /// <summary>Identifies the <see cref="AspectRatio"/> property.</summary>
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(AspectRatioTile),
        new FrameworkPropertyMetadata(4.0 / 3, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double ratio && ratio > 0 && !double.IsInfinity(ratio));

    /// <summary>Identifies the <see cref="Background"/> property.</summary>
    public static readonly DependencyProperty BackgroundProperty = Panel.BackgroundProperty.AddOwner(
        typeof(AspectRatioTile), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The tile's width divided by its height. Default 4:3.</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>What shows behind the content.</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>The height a tile <paramref name="width"/> wide has, at its aspect ratio.</summary>
    public static double HeightFor(double width, double aspectRatio) => width / aspectRatio;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsInfinity(constraint.Width) ? 160 : constraint.Width;
        var size = new Size(width, HeightFor(width, AspectRatio));
        Child?.Measure(size);
        return size;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is { } background)
        {
            drawingContext.DrawRectangle(background, null, new Rect(RenderSize));
        }
    }
}
