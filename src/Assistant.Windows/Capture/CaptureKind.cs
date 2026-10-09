namespace Assistant.Windows.Capture;

/// <summary>What a <see cref="CapturedImage"/> is a picture of.</summary>
public enum CaptureKind
{
    /// <summary>A whole monitor.</summary>
    Monitor = 0,

    /// <summary>One window, as the window draws itself.</summary>
    Window = 1,

    /// <summary>A rectangle of the screen, such as the part of a snapshot that the user selected.</summary>
    Region = 2,
}

/// <summary>How the pixels of a <see cref="CapturedImage"/> were taken.</summary>
public enum CaptureMethod
{
    /// <summary>Copied from the screen as it is composed, so it shows what the user sees, windows in front of it included.</summary>
    ScreenCopy = 0,

    /// <summary>Rendered by the window itself, so what covers it on screen, or lies off screen, does not show.</summary>
    WindowRender = 1,
}
