using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Controls;

/// <summary>
/// Lays its children out as a gallery: square tiles <see cref="Columns"/> across the full width, <see cref="Spacing"/>
/// apart, filling rows from the left and continuing in rows below for as many as there are.
/// </summary>
public sealed class GalleryPanel : Panel
{
    /// <summary>Identifies the <see cref="Columns"/> property.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(GalleryPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int and > 0);

    /// <summary>Identifies the <see cref="Spacing"/> property.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(GalleryPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double spacing && spacing >= 0 && double.IsFinite(spacing));

    // How wide the gallery is when nothing limits it: tiles of this size.
    private const double UnconstrainedTileSize = 128;

    /// <summary>How many tiles fit across.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The gap between neighboring tiles, across and down, in DIPs.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The side of a tile when the gallery is <paramref name="width"/> DIPs wide.</summary>
    public double TileSize(double width) => Math.Max(0, (width - (Columns - 1) * Spacing) / Columns);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? Columns * UnconstrainedTileSize + (Columns - 1) * Spacing
            : availableSize.Width;
        var tile = TileSize(width);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(tile, tile));
        }

        return new Size(width, HeightFor(tile));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var tile = TileSize(finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var (row, column) = Math.DivRem(i, Columns);
            InternalChildren[i].Arrange(new Rect(column * (tile + Spacing), row * (tile + Spacing), tile, tile));
        }

        return finalSize;
    }

    private double HeightFor(double tile)
    {
        var rows = (InternalChildren.Count + Columns - 1) / Columns;
        return rows == 0 ? 0 : rows * tile + (rows - 1) * Spacing;
    }
}
