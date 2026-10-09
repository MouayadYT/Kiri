namespace Assistant.Windows.Backdrop;

/// <summary>Creates blurred backdrops for top-level windows (PROJECT_SPEC §4.0, §9.2 R5).</summary>
public interface IWindowBackdropFactory
{
    /// <summary>
    /// Creates a backdrop for <paramref name="window"/>, a top-level window on the calling thread. Create it before
    /// the window is first shown. The backdrop follows the window as it moves, shows, hides and changes topmost
    /// state, and is destroyed with it.
    /// </summary>
    /// <returns>
    /// The backdrop. If blur is unavailable on this system, a backdrop that is never blurred and has no window.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The calling thread is not the thread that created the first backdrop.
    /// </exception>
    IWindowBackdrop Create(nint window);
}
