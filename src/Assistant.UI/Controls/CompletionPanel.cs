using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Controls;

/// <summary>
/// Lays out what the Search or Ask field draws after the typed text (PROJECT_SPEC §4.1): an invisible copy of the typed text, which
/// takes the room that text takes, and, right after it, the plate that holds the rest of the highlighted result's name. The plate is
/// drawn only when it fits in the room there is; a long query that leaves no room for it shows no plate rather than one that runs
/// under the field's buttons.
/// </summary>
public sealed class CompletionPanel : Panel
{
    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(Math.Min(width, double.IsInfinity(availableSize.Width) ? width : availableSize.Width), height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var size = child.DesiredSize;

            // The first child is the typed text's copy, which always has its place; the others only when they fit.
            var fits = i == 0 || x + size.Width <= finalSize.Width + 0.5;
            child.Arrange(fits
                ? new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height)
                : new Rect(0, 0, 0, 0));
            if (fits)
            {
                x += size.Width;
            }
        }

        return finalSize;
    }
}
