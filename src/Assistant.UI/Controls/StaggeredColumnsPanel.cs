using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Controls;

/// <summary>
/// Lays its children out in columns of equal width, each child going under the shortest column so far, in order, so that cards of
/// different heights fit together without gaps, as in the image search results reference. It does not virtualize: it is for a handful of
/// cards. A child is as wide as its column and as tall as it wants to be.
/// </summary>
public sealed class StaggeredColumnsPanel : Panel
{
    /// <summary>Identifies the <see cref="Columns"/> property.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(StaggeredColumnsPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => (int)value >= 1);

    /// <summary>Identifies the <see cref="ColumnSpacing"/> property.</summary>
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(StaggeredColumnsPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>How many columns there are. Default 2.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The space between columns, in DIPs. The children keep their own bottom margins for the space between rows.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Where each child goes, for a column width and the heights the children measured: its column, and its top.</summary>
    internal static (int Column, double Top)[] Place(IReadOnlyList<double> heights, int columns)
    {
        var bottoms = new double[columns];
        var places = new (int Column, double Top)[heights.Count];
        for (var index = 0; index < heights.Count; index++)
        {
            // The leftmost of the shortest columns, so equal columns fill left to right.
            var column = 0;
            for (var candidate = 1; candidate < columns; candidate++)
            {
                if (bottoms[candidate] < bottoms[column] - 0.001)
                {
                    column = candidate;
                }
            }

            places[index] = (column, bottoms[column]);
            bottoms[column] += heights[index];
        }

        return places;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ColumnWidth(availableSize.Width);
        var heights = new List<double>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            heights.Add(child.DesiredSize.Height);
        }

        var places = Place(heights, Columns);
        var bottom = 0.0;
        for (var index = 0; index < places.Length; index++)
        {
            bottom = Math.Max(bottom, places[index].Top + heights[index]);
        }

        var used = double.IsInfinity(availableSize.Width) ? Columns * width + (Columns - 1) * ColumnSpacing : availableSize.Width;
        return new Size(used, bottom);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = ColumnWidth(finalSize.Width);
        var heights = InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize.Height).ToList();
        var places = Place(heights, Columns);
        for (var index = 0; index < places.Length; index++)
        {
            InternalChildren[index].Arrange(new Rect(places[index].Column * (width + ColumnSpacing), places[index].Top, width, heights[index]));
        }

        return finalSize;
    }

    private double ColumnWidth(double available) =>
        double.IsInfinity(available) ? 160 : Math.Max(0, (available - (Columns - 1) * ColumnSpacing) / Columns);
}
