namespace Assistant.Windows.Frame;

/// <summary>
/// The frame Windows draws for a full application window with custom chrome: its shadow, its rounded corners, its
/// hairline border and a system backdrop behind the whole window, dark to match the Assistant's glass. The window
/// draws its own surfaces over the backdrop, translucent while <see cref="IsTranslucent"/> is true and opaque
/// otherwise (PROJECT_SPEC §4.0).
/// </summary>
/// <remarks>Use and dispose it on the thread that owns the window. It is released with the window.</remarks>
public interface IWindowFrame : IDisposable
{
    /// <summary>
    /// Whether the backdrop shows through the window's translucent surfaces. It is <see langword="false"/> while Windows
    /// transparency effects are off or high contrast is on, and always when the system has no backdrops.
    /// </summary>
    bool IsTranslucent { get; }

    /// <summary>Raised on the window's thread when <see cref="IsTranslucent"/> changes.</summary>
    event EventHandler? IsTranslucentChanged;
}
