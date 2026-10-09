using System.Windows;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// Clips an element to everything outside the smooth corners of a <see cref="PanelShape"/> of its own size, reaching
/// <see cref="GetReach"/> DIPs beyond its edges. Put a shape with a shadow effect inside it to cast the shadow of a
/// translucent surface only around it, never through it.
/// </summary>
public static class OutsideClip
{
    /// <summary>Identifies the CornerSize attached property: how far the outline's corners reach along its edges.</summary>
    public static readonly DependencyProperty CornerSizeProperty = DependencyProperty.RegisterAttached(
        "CornerSize", typeof(double), typeof(OutsideClip), new PropertyMetadata(double.NaN, OnChanged));

    /// <summary>Identifies the Reach attached property: how far beyond the element's edges its content still shows.</summary>
    public static readonly DependencyProperty ReachProperty = DependencyProperty.RegisterAttached(
        "Reach", typeof(double), typeof(OutsideClip), new PropertyMetadata(48.0, OnChanged));

    /// <summary>Gets how far the outline's corners reach, or NaN when the element is not clipped.</summary>
    public static double GetCornerSize(FrameworkElement element) => (double)element.GetValue(CornerSizeProperty);

    /// <summary>Clips the element to the outside of an outline whose corners reach <paramref name="value"/> DIPs.</summary>
    public static void SetCornerSize(FrameworkElement element, double value) => element.SetValue(CornerSizeProperty, value);

    /// <summary>Gets how far beyond the element's edges its content still shows, in DIPs.</summary>
    public static double GetReach(FrameworkElement element) => (double)element.GetValue(ReachProperty);

    /// <summary>Sets how far beyond the element's edges its content still shows, in DIPs.</summary>
    public static void SetReach(FrameworkElement element, double value) => element.SetValue(ReachProperty, value);

    /// <summary>The area outside a <paramref name="size"/> outline with <paramref name="cornerSize"/> corners, out to <paramref name="reach"/>.</summary>
    public static Geometry CreateGeometry(Size size, double cornerSize, double reach)
    {
        var outline = new Rect(size);
        var outer = outline;
        outer.Inflate(reach, reach);
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer),
            PanelShape.CreateGeometry(outline, Math.Max(0, cornerSize)));
        clip.Freeze();
        return clip;
    }

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if (double.IsNaN(GetCornerSize(element)))
        {
            element.ClearValue(UIElement.ClipProperty);
            return;
        }

        element.SizeChanged += OnSizeChanged;
        Update(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element) =>
        element.Clip = CreateGeometry(element.RenderSize, GetCornerSize(element), Math.Max(0, GetReach(element)));
}
