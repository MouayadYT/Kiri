using Assistant.Windows.Interop;

namespace Assistant.Windows.Input;

/// <summary>The user's text-cursor blink setting (Settings, Accessibility, Text cursor).</summary>
public static class CaretBlink
{
    // GetCaretBlinkTime reports INFINITE when blinking is turned off, and 0 when it fails.
    private const uint Infinite = uint.MaxValue;
    private static readonly TimeSpan Default = TimeSpan.FromMilliseconds(530);

    /// <summary>How long a caret stays on, and then off, or <see langword="null"/> when it should not blink.</summary>
    public static TimeSpan? Interval => User32.GetCaretBlinkTime() switch
    {
        Infinite => null,
        0 => Default,
        var milliseconds => TimeSpan.FromMilliseconds(milliseconds),
    };
}
