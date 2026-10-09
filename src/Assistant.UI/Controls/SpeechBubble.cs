using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A rounded bubble around one child, with a small tail curling down from its bottom-right corner, as the user's
/// messages appear in the floating conversation reference. The tail is drawn below the bubble's layout bounds, so
/// leave room for <see cref="TailDepth"/> beneath it.
/// </summary>
public sealed class SpeechBubble : Decorator
{
    /// <summary>How far the tail reaches below the bubble, in DIPs.</summary>
    public const double TailDepth = 5.5;

    /// <summary>Identifies the <see cref="Background"/> property.</summary>
    public static readonly DependencyProperty BackgroundProperty = Panel.BackgroundProperty.AddOwner(
        typeof(SpeechBubble), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Padding"/> property.</summary>
    public static readonly DependencyProperty PaddingProperty = Control.PaddingProperty.AddOwner(
        typeof(SpeechBubble), new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Identifies the <see cref="CornerRadius"/> property.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(SpeechBubble),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The tail, measured from the reference relative to the bubble's bottom-right corner: it leaves the bottom edge
    // this far from the right, runs down to its tip, and returns up into the rounded end.
    private static readonly Vector TailStart = new(-17.5, 0);
    private static readonly Vector TailTip = new(-6.9, TailDepth);
    private static readonly Vector TailEnd = new(-9.5, -2);

    /// <summary>The brush that fills the bubble and its tail.</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Space between the bubble's edge and its child.</summary>
    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>Radius of the bubble's corners, in DIPs. It is limited to half the bubble's height.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Creates the outline of a bubble of <paramref name="size"/>, tail included, as one shape.</summary>
    public static Geometry CreateGeometry(Size size, double cornerRadius)
    {
        var radius = Math.Max(0, Math.Min(cornerRadius, Math.Min(size.Width, size.Height) / 2));
        var body = new RectangleGeometry(new Rect(size), radius, radius);

        var corner = new Point(size.Width, size.Height);
        var tail = new StreamGeometry();
        using (var context = tail.Open())
        {
            context.BeginFigure(corner + TailStart, isFilled: true, isClosed: true);
            context.BezierTo(corner + new Vector(-14, 1.6), corner + new Vector(-10.2, 3.7), corner + TailTip, true, true);
            context.BezierTo(corner + new Vector(-7.4, 3.2), corner + new Vector(-8, 0.8), corner + TailEnd, true, true);
        }

        // One shape, so a translucent fill is not doubled where the tail meets the bubble.
        var outline = Geometry.Combine(body, tail, GeometryCombineMode.Union, null);
        outline.Freeze();
        return outline;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        var padding = Padding;
        var horizontal = padding.Left + padding.Right;
        var vertical = padding.Top + padding.Bottom;
        if (Child is not { } child)
        {
            return new Size(horizontal, vertical);
        }

        child.Measure(new Size(Math.Max(0, constraint.Width - horizontal), Math.Max(0, constraint.Height - vertical)));
        return new Size(child.DesiredSize.Width + horizontal, child.DesiredSize.Height + vertical);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var padding = Padding;
        Child?.Arrange(new Rect(
            padding.Left, padding.Top,
            Math.Max(0, arrangeSize.Width - padding.Left - padding.Right),
            Math.Max(0, arrangeSize.Height - padding.Top - padding.Bottom)));
        return arrangeSize;
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is { } background && RenderSize.Width > 0 && RenderSize.Height > 0)
        {
            drawingContext.DrawGeometry(background, null, CreateGeometry(RenderSize, CornerRadius));
        }
    }
}
