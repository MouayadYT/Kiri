namespace Assistant.Windows.Frame;

/// <summary>How a window's frame looks.</summary>
/// <param name="Backdrop">The material Windows draws behind the whole window.</param>
/// <param name="BorderColor">
/// The hairline border's color as 0xRRGGBB, or <see langword="null"/> for the system's dark border.
/// </param>
public sealed record WindowFrameStyle(SystemBackdropKind Backdrop, int? BorderColor = null);

/// <summary>The materials Windows 11 can draw behind a window.</summary>
public enum SystemBackdropKind
{
    /// <summary>Mica: the desktop wallpaper, blurred and tinted, for long-lived windows.</summary>
    Mica,

    /// <summary>Mica Alt: Mica more strongly tinted by the wallpaper.</summary>
    MicaAlt,

    /// <summary>Acrylic: whatever lies behind the window, blurred, while the window is active.</summary>
    Acrylic,
}
