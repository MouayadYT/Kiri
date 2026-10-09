namespace Assistant.UI.Animation;

/// <summary>
/// How the Search or Ask pill grows into the floating conversation: the glass eases from one form into the other while
/// the pill's contents fade out, and once they are gone the conversation's contents fade in. Declared as a theme token
/// (Themes/Tokens/Motion.xaml). Progress is how far the glass has come, from 0 (the pill) to 1 (the panel), after
/// easing; the contents follow it, so they always keep pace with the glass.
/// </summary>
public sealed record SurfaceMorphMotion
{
    /// <summary>Time for the whole growth, from the pill to the panel.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The progress by which the pill's contents have faded out completely.</summary>
    public double CompactFadeOutEnd { get; init; } = 0.4;

    /// <summary>The progress at which the conversation's contents begin to fade in, once the pill's are gone.</summary>
    public double ConversationFadeInStart { get; init; } = 0.4;

    /// <summary>The progress by which the conversation's contents are fully shown.</summary>
    public double ConversationFadeInEnd { get; init; } = 0.9;

    /// <summary>Opacity of the pill's contents, such as the typed text and the microphone, at a progress.</summary>
    public double CompactOpacityAt(double progress) => 1 - Ramp(progress, 0, CompactFadeOutEnd);

    /// <summary>Opacity of the conversation's contents at a progress.</summary>
    public double ConversationOpacityAt(double progress) =>
        Ramp(progress, ConversationFadeInStart, ConversationFadeInEnd);

    // 0 up to the start, 1 from the end on, and straight in between.
    private static double Ramp(double progress, double start, double end) =>
        progress <= start ? 0 : progress >= end ? 1 : (progress - start) / (end - start);
}
