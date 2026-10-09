namespace Assistant.Windows.Placement;

/// <summary>
/// A point in physical screen pixels, in the coordinate space of the calling thread's DPI awareness like every screen
/// coordinate.
/// </summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public readonly record struct ScreenPoint(int X, int Y);
