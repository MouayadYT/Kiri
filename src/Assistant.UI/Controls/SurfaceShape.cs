using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// The Assistant's glass, in whatever <see cref="SurfaceForm"/> it has now. It fills its layout slot, and its stroke is
/// drawn inside the bounds. Giving it a form between the pill's and the panel's draws the surface part way through
/// growing from one into the other.
/// </summary>
public sealed class SurfaceShape : GlassShape
{
    /// <summary>Identifies the <see cref="Form"/> property.</summary>
    public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
        nameof(Form), typeof(SurfaceForm), typeof(SurfaceShape),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How the surface's corners curve. Without one the surface is a plain rectangle.</summary>
    public SurfaceForm? Form
    {
        get => (SurfaceForm?)GetValue(FormProperty);
        set => SetValue(FormProperty, value);
    }

    /// <inheritdoc/>
    protected override Geometry CreateOutline(Rect bounds) =>
        Form?.CreateGeometry(bounds) ?? new RectangleGeometry(bounds);
}
