using System.Windows;
using System.Windows.Controls;
using Assistant.UI.Messages;

namespace Assistant.UI.Controls;

/// <summary>
/// Stacks the blocks of a message's prose, or the parts of a message, top to bottom, <see cref="Spacing"/> apart.
/// A heading has <see cref="HeadingSpacing"/> above it instead, and a wide part, such as a card or a gallery
/// (<see cref="MessageContent.IsWide"/>), has <see cref="CardSpacing"/> between it and a neighbor that is not wide.
/// The first child starts at the top, so a message begins exactly where its template puts it, and children that are
/// collapsed or empty, such as prose that has not streamed in yet, take no space. A child's kind is its data, as for
/// the containers an <see cref="ItemsControl"/> generates.
/// </summary>
public sealed class MessageBlockPanel : Panel
{
    /// <summary>Identifies the <see cref="Spacing"/> property.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(MessageBlockPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidSpacing);

    /// <summary>Identifies the <see cref="HeadingSpacing"/> property.</summary>
    public static readonly DependencyProperty HeadingSpacingProperty = DependencyProperty.Register(
        nameof(HeadingSpacing), typeof(double), typeof(MessageBlockPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidSpacing);

    /// <summary>Identifies the <see cref="CardSpacing"/> property.</summary>
    public static readonly DependencyProperty CardSpacingProperty = DependencyProperty.Register(
        nameof(CardSpacing), typeof(double), typeof(MessageBlockPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidSpacing);

    /// <summary>The gap between one child and the next, in DIPs.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap above a heading that follows another block, in DIPs.</summary>
    public double HeadingSpacing
    {
        get => (double)GetValue(HeadingSpacingProperty);
        set => SetValue(HeadingSpacingProperty, value);
    }

    /// <summary>The gap between a wide part, such as a card, and a neighbor that is not wide, in DIPs.</summary>
    public double CardSpacing
    {
        get => (double)GetValue(CardSpacingProperty);
        set => SetValue(CardSpacingProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(constraint);
        }

        var width = 0.0;
        var height = 0.0;
        foreach (var (child, top) in Layout())
        {
            height = top + child.DesiredSize.Height;
            width = Math.Max(width, child.DesiredSize.Width);
        }

        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, top) in Layout())
        {
            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
        }

        return finalSize;
    }

    // The children that take space, each with its top. Empty children are arranged at their place without a gap.
    private IEnumerable<(UIElement Child, double Top)> Layout()
    {
        var top = 0.0;
        UIElement? previous = null;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (child.DesiredSize.Height <= 0)
            {
                yield return (child, top);
                continue;
            }

            // A part that is folding away or back (Fold) takes its gap with it.
            top += previous is null ? 0 : SpacingBetween(previous, child) * Math.Clamp(Fold.GetAmount(child), 0, 1);
            yield return (child, top);
            top += child.DesiredSize.Height;
            previous = child;
        }
    }

    private double SpacingBetween(UIElement previous, UIElement child)
    {
        if (DataOf(child) is HeadingBlock)
        {
            return HeadingSpacing;
        }

        return IsWide(previous) != IsWide(child) ? CardSpacing : Spacing;
    }

    private static bool IsWide(UIElement child) => DataOf(child) is MessageContent { IsWide: true };

    private static object? DataOf(UIElement child) => (child as FrameworkElement)?.DataContext;

    private static bool IsValidSpacing(object value) => value is double spacing && spacing >= 0 && double.IsFinite(spacing);
}
