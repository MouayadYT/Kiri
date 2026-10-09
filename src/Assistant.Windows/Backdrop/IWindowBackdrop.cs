namespace Assistant.Windows.Backdrop;

/// <summary>
/// A blurred view of whatever lies behind part of a window. It adds no tint: the window draws its own translucent
/// surface over the blur, or an opaque one while <see cref="IsBlurred"/> is <see langword="false"/>.
/// </summary>
/// <remarks>Use and dispose it on the thread that owns the window.</remarks>
public interface IWindowBackdrop : IDisposable
{
    /// <summary>
    /// The window that shows the blur, or zero if there is none. Make it the owner of the target window: an owned
    /// window always stays above its owner, so the blur stays directly beneath the target.
    /// </summary>
    nint Handle { get; }

    /// <summary>
    /// Whether the blur is shown. It is <see langword="false"/> while Windows transparency effects are off or high
    /// contrast is on, and always when the backdrop APIs are unavailable. The window should then draw its surface
    /// opaque (PROJECT_SPEC §4.0).
    /// </summary>
    bool IsBlurred { get; }

    /// <summary>Raised on the window's thread when <see cref="IsBlurred"/> changes.</summary>
    event EventHandler? IsBlurredChanged;

    /// <summary>Sets the part of the window to blur. The blur is hidden while the region is empty.</summary>
    void SetRegion(BackdropRegion region);

    /// <summary>
    /// Sets how strongly the blur shows, from 0 (not at all) to 1 (fully, the default), so it can fade with the
    /// window's glass.
    /// </summary>
    void SetOpacity(double opacity);
}
