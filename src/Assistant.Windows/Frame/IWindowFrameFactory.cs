namespace Assistant.Windows.Frame;

/// <summary>Gives full application windows the frame Windows draws for them (PROJECT_SPEC §4.0, §9.2 R5).</summary>
public interface IWindowFrameFactory
{
    /// <summary>
    /// Gives <paramref name="window"/>, a top-level window on the calling thread whose client area covers the whole
    /// window, a dark frame in <paramref name="style"/>. Apply it before the window is first shown.
    /// </summary>
    /// <returns>The frame. If the system has no backdrops, a frame that is never translucent.</returns>
    IWindowFrame Apply(nint window, WindowFrameStyle style);
}
