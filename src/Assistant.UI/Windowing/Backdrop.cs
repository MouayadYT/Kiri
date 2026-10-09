using System.Windows;

namespace Assistant.UI.Windowing;

/// <summary>Attached state that lets styles react to the blurred backdrop behind a window.</summary>
public static class Backdrop
{
    private static readonly DependencyPropertyKey IsBlurredPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsBlurred", typeof(bool), typeof(Backdrop),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>
    /// Identifies the IsBlurred attached property. It is set on a window by <see cref="BackdropHost"/> and inherited
    /// by every element in it, so any style can trigger on it.
    /// </summary>
    public static readonly DependencyProperty IsBlurredProperty = IsBlurredPropertyKey.DependencyProperty;

    /// <summary>
    /// Gets whether glass in the element's window has a blurred backdrop behind it. When it does not, glass must be
    /// drawn opaque (PROJECT_SPEC §4.0).
    /// </summary>
    public static bool GetIsBlurred(DependencyObject element) => (bool)element.GetValue(IsBlurredProperty);

    internal static void SetIsBlurred(DependencyObject element, bool value) =>
        element.SetValue(IsBlurredPropertyKey, value);
}
