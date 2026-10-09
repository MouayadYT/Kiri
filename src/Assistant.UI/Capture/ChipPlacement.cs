using System.Windows;

namespace Assistant.UI.Capture;

/// <summary>Which side of the selection its chips are on.</summary>
internal enum ChipSide
{
    /// <summary>To the right of the selection, as in the reference.</summary>
    Right,

    /// <summary>To the left of it, when there is no room on the right.</summary>
    Left,

    /// <summary>Under it, left edges together, when there is room on neither side.</summary>
    Below,

    /// <summary>Over it, left edges together.</summary>
    Above,

    /// <summary>Inside it, at its bottom right corner, when the selection fills the monitor.</summary>
    Inside,
}

/// <summary>Where the chips go: beside the selection, on the side that has room, never off the monitor.</summary>
internal readonly record struct ChipPlacementResult(Point Position, ChipSide Side);

/// <summary>
/// Decides where the stack of action chips goes next to the selection (PROJECT_SPEC §4.6). In the reference they hang to its right,
/// their middle level with its middle, a little way out. When the monitor has no room there they go to the left, then under and over
/// it, and, for a selection that fills the monitor, inside it; and whichever side, they are kept on the monitor.
/// </summary>
internal static class ChipPlacement
{
    /// <summary>How far the chips are from the selection's edge: the reference's 16 pixels at 2x.</summary>
    public const double Gap = 8;

    /// <summary>How near the monitor's edge the chips may come.</summary>
    public const double Margin = 8;

    /// <summary>Chooses where a stack of chips of <paramref name="chips"/> goes beside <paramref name="selection"/> on a monitor of <paramref name="bounds"/>.</summary>
    public static ChipPlacementResult Place(Rect selection, Size chips, Size bounds)
    {
        var middleY = selection.Top + selection.Height / 2 - chips.Height / 2;
        var rightX = selection.Right + Gap;
        var leftX = selection.Left - Gap - chips.Width;
        var belowY = selection.Bottom + Gap;
        var aboveY = selection.Top - Gap - chips.Height;

        if (rightX + chips.Width <= bounds.Width - Margin)
        {
            return new ChipPlacementResult(KeepOn(new Point(rightX, middleY), chips, bounds), ChipSide.Right);
        }

        if (leftX >= Margin)
        {
            return new ChipPlacementResult(KeepOn(new Point(leftX, middleY), chips, bounds), ChipSide.Left);
        }

        if (belowY + chips.Height <= bounds.Height - Margin)
        {
            return new ChipPlacementResult(KeepOn(new Point(selection.Left, belowY), chips, bounds), ChipSide.Below);
        }

        if (aboveY >= Margin)
        {
            return new ChipPlacementResult(KeepOn(new Point(selection.Left, aboveY), chips, bounds), ChipSide.Above);
        }

        return new ChipPlacementResult(
            KeepOn(new Point(selection.Right - Gap - chips.Width, selection.Bottom - Gap - chips.Height), chips, bounds), ChipSide.Inside);
    }

    // The position moved the least that keeps the chips on the monitor, within its margin (a stack larger than the monitor stays at its corner).
    private static Point KeepOn(Point position, Size chips, Size bounds) =>
        new(
            Math.Max(Margin, Math.Min(position.X, bounds.Width - Margin - chips.Width)),
            Math.Max(Margin, Math.Min(position.Y, bounds.Height - Margin - chips.Height)));
}
